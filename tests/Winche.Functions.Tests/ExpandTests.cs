using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Values;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class ExpandTests(PostgresFixture pg)
{
    private static Dictionary<string, Value> F(params (string, Value)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public async Task Ctx_db_get_and_query_follow_references_with_expand()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<Winche.Storage.Services.FileStorage>().SetAsync("scans/a.png", "image/png", 42);
        string uid;
        await using (var scope = app.App.Services.CreateAsyncScope())
            uid = (await scope.ServiceProvider.GetRequiredService<Winche.Auth.IWincheAuth>().CreateUserAsync("ann@example.com", null, "Ann Lee")).Uid;
        await app.Service<DocumentDatabase>().WriteAsync([
            new SetWrite { Path = "doctors/d1", Fields = F(("name", new StringValue("Dr Who"))) },
            new SetWrite { Path = "patients/p1", Fields = F(("name", new StringValue("Ann")), ("doctor", new DocumentReferenceValue("doctors/d1"))) },
            new SetWrite { Path = "visits/v1", Fields = F(("patient", new DocumentReferenceValue("patients/p1")),
                ("team", new ArrayValue([new DocumentReferenceValue("doctors/d1"), new DocumentReferenceValue("doctors/gone")])), ("ward", new StringValue("icu")),
                ("scan", new FileReferenceValue("scans/a.png")), ("author", new UserReferenceValue(uid))) },
        ]);
        await app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin"; import { Link } from "@winche-technologies/winche-admin";
                export const visit = callable(async (_d, ctx) => {
                  const visit = admin.documents.doc("visits/v1");
                  const v = await visit.expand(["patient.doctor", "team"]).get();
                  const plain = await visit.get();
                  const linked = await visit.expand(["scan", "author"]).get();
                  await admin.documents.doc("notes/n1").set({ visit, scan: admin.files.child("scans/a.png"), by: Link.user("u9") });
                  const q = await admin.documents.collection("visits").where("ward", "==", "icu").expand(["patient"]).get();
                  const patient = (v.get("patient") as Link).data() as any;
                  return {
                    patient: patient.name,
                    doctor: ((patient.doctor as Link).data() as any)?.name,
                    team: (v.get("team") as Link[]).map((t) => (t.data() as any)?.name ?? t.target),
                    plain: (plain.get("patient") as Link).target,
                    plainData: (plain.get("patient") as Link).data(),
                    queried: ((q.docs[0].get("patient") as Link).data() as any).name,
                    queriedDoctor: (((q.docs[0].get("patient") as Link).data() as any).doctor as Link).target,
                    scanSize: ((linked.get("scan") as Link).data() as any).sizeBytes,
                    author: ((linked.get("author") as Link).data() as any).displayName,
                    authorEmail: ((linked.get("author") as Link).data() as any).email,
                  };
                });
                """,
        }, null, null);

        using var response = await app.Http.PostAsJsonAsync($"{app.Url}/functions/v1/call/visit", new { data = (object?)null });
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var data = body["data"] ?? throw new Xunit.Sdk.XunitException(body.ToJsonString());
        Assert.Equal("Ann", (string?)data["patient"]);
        Assert.Equal("Dr Who", (string?)data["doctor"]);
        Assert.Equal(["Dr Who", "doctors/gone"], data["team"]!.AsArray().Select(t => (string?)t));   // a missing document stays a path
        Assert.Equal("patients/p1", (string?)data["plain"]);
        Assert.Null(data["plainData"]);   // not expanded: no data
        var note = await app.Service<DocumentDatabase>().GetAsync("notes/n1");   // references store typed
        Assert.Equal((new DocumentReferenceValue("visits/v1"), new FileReferenceValue("scans/a.png"), new UserReferenceValue("u9")),
            ((DocumentReferenceValue)note!.Fields["visit"], (FileReferenceValue)note.Fields["scan"], (UserReferenceValue)note.Fields["by"]));
        Assert.Equal("Ann", (string?)data["queried"]);
        Assert.Equal("doctors/d1", (string?)data["queriedDoctor"]);   // only "patient" was expanded
        Assert.Equal(42, (int?)data["scanSize"]);
        Assert.Equal("Ann Lee", (string?)data["author"]);
        Assert.Equal("ann@example.com", (string?)data["authorEmail"]);   // functions run with the server's rights
    }
}
