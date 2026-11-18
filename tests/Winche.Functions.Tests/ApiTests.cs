using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Auth;
using Winche.Storage.Services;

namespace Winche.Functions.Tests;

/// <summary>The SDK's admin.documents / admin.files / admin.auth, shaped like the Dart SDKs.</summary>
[Collection("postgres")]
public class ApiTests(PostgresFixture pg)
{
    [Fact]
    public async Task Documents_aggregate_group_transform_and_trigger_with_typed_values()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, onDocument } from "@winche/functions"; import * as admin from "@winche/admin"; import { FieldValue, Filter, Aggregate, GeoPoint, Link } from "@winche/admin";
                export const run = callable(async (_d, ctx) => {
                  const db = admin.documents;
                  const orders = db.collection("shops/s1/orders");
                  await orders.doc("o1").set({ amount: 10, paid: true, tags: ["a"] });
                  await orders.doc("o2").set({ amount: 30, paid: true, tags: ["a", "b"] });
                  await orders.doc("o3").set({ amount: 5, paid: false });
                  await db.doc("shops/s2/orders/o9").set({ amount: 100, paid: true });
                  const added = await orders.add({ amount: 1, paid: false, at: new Date("2026-11-16T10:00:00.123Z"), place: new GeoPoint(59.9, 10.7),
                    bytes: new Uint8Array([1, 2, 3]), big: 2n ** 60n, shop: db.doc("shops/s1") });
                  await orders.doc("o1").update({ tags: FieldValue.arrayUnion("c", "a"), amount: FieldValue.increment(2), paid: FieldValue.delete() });
                  const paid = orders.where("paid", "==", true);
                  const agg = await paid.aggregate({ n: Aggregate.count(), total: Aggregate.sum("amount"), avg: Aggregate.average("amount") });
                  const either = await orders.where(Filter.or(Filter.where("amount", ">=", 30), Filter.where("amount", "==", 5))).orderBy("amount").get();
                  const group = await db.collectionGroup("orders").where("paid", "==", true).orderBy("amount", "desc").get();
                  const back = await added.get();
                  const all = await db.getAll([orders.doc("o1"), orders.doc("nope")]);
                  return {
                    agg, sum: await paid.sum("amount"), either: either.docs.map((d) => d.id), group: group.docs.map((d) => d.reference.path),
                    o1: all[0].data(), missing: all[1].exists,
                    at: (back.get("at") as Date).toISOString(), lat: (back.get("place") as GeoPoint).latitude, bytes: [...back.get("bytes")],
                    big: String(back.get("big")), shop: (back.get("shop") as Link).target, kind: (back.get("shop") as Link).kind,
                    parent: added.parent.path, grand: added.parent.parent?.path,
                  };
                });
                export const stamped = onDocument.created("events/{id}", async (e, ctx) => {
                  const at = e.data.get("at");
                  await admin.documents.doc(`seen/${e.params.id}`).set({ isDate: at instanceof Date, iso: at instanceof Date ? at.toISOString() : null, before: e.before.exists });
                });
                """,
        }, null, null);
        var r = await functions.InvokeAsync("run", null, FunctionCaller.Server);
        Assert.True(r.Ok, r.ErrorMessage);
        var o = r.Result!;
        Assert.Equal("""{"n":1,"total":30,"avg":30}""", o["agg"]!.ToJsonString());   // o1's paid was deleted
        Assert.Equal(30, o["sum"]!.GetValue<int>());
        Assert.Equal("""["o3","o2"]""", o["either"]!.ToJsonString());
        Assert.Equal("""["shops/s2/orders/o9","shops/s1/orders/o2"]""", o["group"]!.ToJsonString());
        Assert.Equal("""{"amount":12,"tags":["a","c"]}""", o["o1"]!.ToJsonString());
        Assert.False(o["missing"]!.GetValue<bool>());
        Assert.Equal(("2026-11-16T10:00:00.123Z", 59.9, "[1,2,3]", "1152921504606846976", "shops/s1", "document"),
            (o["at"]!.GetValue<string>(), o["lat"]!.GetValue<double>(), o["bytes"]!.ToJsonString(), o["big"]!.GetValue<string>(), o["shop"]!.GetValue<string>(), o["kind"]!.GetValue<string>()));
        Assert.Equal(("shops/s1/orders", "shops/s1"), (o["parent"]!.GetValue<string>(), o["grand"]!.GetValue<string>()));

        // A trigger's snapshot carries typed values: a timestamp is a Date there too.
        var db = app.Service<Winche.Database.Runtime.DocumentDatabase>();
        await db.WriteAsync([new Winche.Database.Runtime.Writes.SetWrite { Path = "events/e1", Fields = new Dictionary<string, Winche.Values.Value>
            { ["at"] = new Winche.Values.TimestampValue(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero)) } }]);
        Winche.Database.Documents.Document? seen = null;
        for (var i = 0; i < 200 && (seen = await db.GetAsync("seen/e1")) is null; i++) await Task.Delay(100);
        Assert.NotNull(seen);
        Assert.Equal(new Winche.Values.BooleanValue(true), seen!.Fields["isDate"]);
        Assert.Equal(new Winche.Values.StringValue("2026-11-16T09:00:00.000Z"), seen.Fields["iso"]);
        Assert.Equal(new Winche.Values.BooleanValue(false), seen.Fields["before"]);
    }

    [Fact]
    public async Task Files_come_from_a_url_and_users_have_claims()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();

        // A source that wants an authorization header, like a bucket.
        var port = Random.Shared.Next(20000, 40000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var big = new byte[3_000_000];
        new Random(5).NextBytes(big);
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                if (c.Request.Headers["Authorization"] != "Bearer good") { c.Response.StatusCode = 403; c.Response.Close(); continue; }
                c.Response.ContentType = "video/mp4";
                await c.Response.OutputStream.WriteAsync(big);
                c.Response.Close();
            }
        });

        string uid;
        await using (var scope = app.App.Services.CreateAsyncScope())
            uid = (await scope.ServiceProvider.GetRequiredService<IWincheAuth>().CreateUserAsync("ann@example.com", "correct horse battery")).Uid;

        await functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = $$"""
                import { callable } from "@winche/functions"; import * as admin from "@winche/admin";
                export const copy = callable(async (d: { good: boolean }, ctx) => {
                  const f = await admin.files.child("media/clip").putFromUrl("http://127.0.0.1:{{port}}/clip.mp4", { headers: { authorization: d.good ? "Bearer good" : "Bearer bad" }, metadata: { from: "gcs" } });
                  return { size: f.data!.sizeBytes, type: f.data!.mimeType, from: f.data!.metadata.from };
                });
                export const claims = callable(async (d: { uid: string }, ctx) => {
                  const user = admin.auth.user(d.uid);
                  await user.setCustomClaims({ ...(await user.getCustomClaims()), role: "admin" });
                  const snap = await user.get();
                  const nobody = await admin.auth.user("nobody").get();
                  let missing = "";
                  try { await admin.auth.user("nobody").getCustomClaims(); } catch (e) { missing = (e as { code: string }).code; }
                  return { exists: snap.exists, email: snap.data?.email, role: snap.customClaims.role, created: snap.data?.createdAt instanceof Date, nobody: nobody.exists, missing };
                });
                """,
        }, null, null);

        var r = await functions.InvokeAsync("copy", JsonNode.Parse("""{"good":true}"""), FunctionCaller.Server);
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal((3_000_000, "video/mp4", "gcs"), (r.Result!["size"]!.GetValue<int>(), r.Result["type"]!.GetValue<string>(), r.Result["from"]!.GetValue<string>()));
        await using (var stream = await app.Service<FileStorage>().OpenReadAsync("media/clip"))
        {
            using var copy = new MemoryStream();
            await stream!.CopyToAsync(copy);
            Assert.True(copy.ToArray().AsSpan().SequenceEqual(big));
        }
        var refused = await functions.InvokeAsync("copy", JsonNode.Parse("""{"good":false}"""), FunctionCaller.Server);
        Assert.Equal("unavailable", refused.ErrorCode);

        var c = await functions.InvokeAsync("claims", new JsonObject { ["uid"] = uid }, FunctionCaller.Server);
        Assert.True(c.Ok, c.ErrorMessage);
        Assert.Equal("""{"exists":true,"email":"ann@example.com","role":"admin","created":true,"nobody":false,"missing":"not-found"}""", c.Result!.ToJsonString());
        listener.Stop();
    }
}
