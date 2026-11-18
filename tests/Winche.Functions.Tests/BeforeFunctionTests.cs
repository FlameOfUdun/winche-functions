using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Auth;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Storage.Services;
using Winche.Values;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class BeforeFunctionTests(PostgresFixture pg)
{
    private const string Code = """
        import { onDocument, onFile, onUser, HttpsError } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";

        export const guardRecords = onDocument.willBeWritten("records/{id}", async (e, ctx) => {
          if (e.before.get("signedOff")) throw new HttpsError("permission-denied", `Record ${e.params.id} is signed off.`);
          if (e.operation === "delete") return;
          const ward = await admin.documents.doc("settings/ward").get();   // reads work
          return { set: { updatedBy: e.caller?.uid ?? "server", ward: ward.get("name") ?? null, "meta.op": e.operation }, remove: ["draft"] };
        });

        export const sneaky = onDocument.willBeWritten("sneaky/{id}", async (_e, ctx) => {
          await admin.documents.doc("elsewhere/x").set({ a: 1 });   // writes don't: it fails, so the write is refused
        });

        export const lenient = onDocument.willBeWritten("lenient/{id}", async () => { throw new Error("bug"); }, { failOpen: true });

        export const scans = onFile.willBeWritten("scans/{rest=**}", async (e) => {
          if (e.before.data?.metadata?.locked) throw new HttpsError("permission-denied", "Locked.");
          if (e.operation === "delete") return;
          return { metadata: { checked: true, secret: null }, cacheControl: "private" };
        });

        export const noBlocked = onUser.willBeCreated(async (e) => {
          if (e.email?.endsWith("@blocked.example")) throw new HttpsError("permission-denied", "Sign-ups from that domain are closed.");
        });

        export const noNightOwls = onUser.willSignIn(async (e) => {
          if (e.user.data?.displayName === "Owl") throw new HttpsError("permission-denied", "Not now, Owl.");
        });
        """;

    private static Dictionary<string, Value> F(params (string, object?)[] f) => f.ToDictionary(x => x.Item1, x => Value.FromObject(x.Item2));

    [Fact]
    public async Task Before_functions_refuse_and_change_writes_files_and_sign_ups()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, null, null);
        var db = app.Service<DocumentDatabase>();
        await db.WriteAsync([new SetWrite { Path = "settings/ward", Fields = F(("name", "ICU")) }]);

        // Changed: fields set and removed, with a read from admin.documents
        await db.WriteAsync([new SetWrite { Path = "records/r1", Fields = F(("n", 1), ("draft", "x")) }]);
        var r1 = (await db.GetAsync("records/r1"))!.Fields;
        Assert.Equal(("server", "ICU", false), (((StringValue)r1["updatedBy"]).Value, ((StringValue)r1["ward"]).Value, r1.ContainsKey("draft")));
        Assert.Equal("create", ((StringValue)((MapValue)r1["meta"]).Fields["op"]).Value);

        // Refused, with the function's message
        await db.WriteAsync([new SetWrite { Path = "records/r2", Fields = F(("signedOff", true)) }]);
        var denied = await Assert.ThrowsAsync<WincheDeniedException>(() => db.WriteAsync([new DeleteWrite { Path = "records/r2" }]));
        Assert.Equal("Record r2 is signed off.", denied.Message);
        Assert.NotNull(await db.GetAsync("records/r2"));

        // admin.documents can't write in a before-function: the function fails, so the write is refused (fail closed)…
        var sneaky = await Assert.ThrowsAsync<WincheDeniedException>(() => db.WriteAsync([new SetWrite { Path = "sneaky/s", Fields = F(("a", 1)) }]));
        Assert.Contains("sneaky failed", sneaky.Message);
        Assert.Null(await db.GetAsync("elsewhere/x"));
        // …unless it's failOpen
        await db.WriteAsync([new SetWrite { Path = "lenient/l", Fields = F(("a", 1)) }]);
        Assert.NotNull(await db.GetAsync("lenient/l"));

        // Files: metadata and headers changed, a locked one refused
        var storage = app.Service<FileStorage>();
        var scan = await storage.SetAsync("scans/a.png", "image/png", 10, new JsonObject { ["secret"] = "s" });
        Assert.Equal((true, false, "private"), ((bool)scan.MetaData["checked"]!, scan.MetaData.ContainsKey("secret"), scan.CacheControl));
        await storage.UpdateMetadataAsync("scans/a.png", new JsonObject { ["locked"] = true });
        Assert.Equal("Locked.", (await Assert.ThrowsAsync<WincheDeniedException>(() => storage.DeleteAsync("scans/a.png"))).Message);

        // Users: sign-up and sign-in refused with the function's message
        await using var scope = app.App.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IWincheAuth>();
        var info = new AuthRequestInfo("127.0.0.1", null);
        var blocked = await Assert.ThrowsAsync<AuthDeniedException>(() => auth.SignUpAsync("x@blocked.example", "correct horse battery", null, info));
        Assert.Equal("Sign-ups from that domain are closed.", blocked.Message);
        await auth.SignUpAsync("owl@example.com", "correct horse battery", "Owl", info);
        Assert.Equal("Not now, Owl.", (await Assert.ThrowsAsync<AuthDeniedException>(() => auth.SignInAsync("owl@example.com", "correct horse battery", info))).Message);
    }

    [Fact]
    public async Task A_before_function_must_be_quick()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { onDocument } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";
                export const slow = onDocument.willBeWritten("a/{id}", async () => {}, { timeoutSeconds: 60 });
                export const nowhere = onDocument.willBeWritten("", async () => {});
                """,
        }, null, null));
        Assert.Contains(bad.Problems, p => p.StartsWith("slow: timeoutSeconds is 1 to 30"));
        Assert.Contains(bad.Problems, p => p.StartsWith("nowhere: give a path"));
    }
}
