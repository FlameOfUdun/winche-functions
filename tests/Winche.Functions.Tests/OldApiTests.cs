namespace Winche.Functions.Tests;

[Collection("postgres")]
public class OldApiTests(PostgresFixture pg)
{
    [Fact]
    public async Task Code_using_the_functions_API_from_before_the_admin_SDK_is_refused_with_what_replaces_it()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { documents, users, callable, consolePage, consoleAction } from "@winche/functions";
                export const a = documents.onCreated("x/{id}", async (e, ctx) => { await ctx.documents.doc("y/1").set({}); });
                export const b = users.beforeSignIn(async () => {});
                export const c = callable(async (_d, ctx) => ctx.secrets.get("KEY") + ctx.functionName);
                export const p = consolePage({ title: "P", files: "console/p" });
                export const q = consoleAction(async () => 1);
                """,
        }, null, null));
        Assert.Equal("This code uses the functions API from before @winche/admin.", bad.Message);
        Assert.Contains("index.ts:2: import { documents } from \"@winche/admin\" instead of ctx.documents.", bad.Problems);
        Assert.Contains("index.ts:2: onDocument.created (from @winche/functions) instead of documents.onCreated.", bad.Problems);
        Assert.Contains("index.ts:3: onUser.willSignIn (from @winche/functions) instead of users.beforeSignIn.", bad.Problems);
        Assert.Contains(bad.Problems, p => p.StartsWith("index.ts:4: declare the secret once"));
        Assert.Contains("index.ts:4: ctx.name instead of ctx.functionName.", bad.Problems);
        Assert.Contains("index.ts:5: pages({ title, files }) instead of consolePage.", bad.Problems);
        Assert.Contains(bad.Problems, p => p.StartsWith("index.ts:6: console actions are gone"));
    }
}
