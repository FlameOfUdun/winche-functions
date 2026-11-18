using System.Text.Json.Nodes;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class AdminSdkTests(PostgresFixture pg)
{
    [Fact]
    public async Task Functions_call_callables_as_the_server_or_as_a_user_and_as_is_refused_where_rules_dont_apply()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, HttpsError } from "@winche/functions";
                import { functions, as, auth, documents } from "@winche/admin";
                export const whoami = callable(async (_d, ctx) => ({ uid: ctx.caller?.uid ?? null, name: ctx.name }));
                export const refuse = callable(async () => { throw new HttpsError("failed-precondition", "not now", { why: 1 }); });
                export const outer = callable(async (_d, ctx) => ({
                  server: await functions.call("whoami"),
                  user: await as(ctx.caller!).functions.call("whoami"),
                  refused: await functions.call("refuse").catch((e) => `${e.code}: ${e.message}`),
                  missing: await functions.call("nope").catch((e) => e.code),
                  authAs: await as(ctx.caller!).auth.user("u1").get().catch((e) => e.code),
                }));
                // Outside a run (a module's top level) the SDK says how to use it.
                let early = "";
                try { await documents.doc("a/b").get(); } catch (e) { early = (e as Error).message; }
                export const topLevel = callable(async () => early);
                """,
        }, null, null);

        var r = await functions.InvokeAsync("outer", null, new FunctionCaller("ann", new Dictionary<string, object?> { ["sub"] = "ann" }));
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal("""{"uid":null,"name":"whoami"}""", r.Result!["server"]!.ToJsonString());
        Assert.Equal("ann", r.Result["user"]!["uid"]!.GetValue<string>());
        Assert.Equal("failed-precondition: not now", r.Result["refused"]!.GetValue<string>());
        Assert.Equal("not-found", r.Result["missing"]!.GetValue<string>());
        Assert.Equal("invalid-argument", r.Result["authAs"]!.GetValue<string>());
        Assert.Contains("isn't signed in here", (await functions.InvokeAsync("topLevel", null, FunctionCaller.Server)).Result!.GetValue<string>());
    }
}
