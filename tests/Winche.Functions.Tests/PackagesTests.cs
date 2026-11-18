namespace Winche.Functions.Tests;

/// <summary>The SDKs are the project's: its deno.json names them (or the host provides them); the protocol is checked.</summary>
[Collection("postgres")]
public class PackagesTests(PostgresFixture pg)
{
    private const string Hello = """
        import { callable } from "@winche/functions";
        import { documents } from "@winche/admin";
        export const hello = callable(async () => (await documents.doc("a/b").get()).exists ? "there" : "not there");
        """;

    [Fact]
    public void Code_that_imports_the_SDKs_without_naming_them_is_told_what_to_add()
    {
        var bundler = new FunctionBundler(new FunctionsOptions());
        Assert.Equal([
            "The code imports @winche/admin, which deno.json doesn't name: add \"@winche/admin\": \"npm:@winche-technologies/admin@^3\" to its imports.",
            "The code imports @winche/functions, which deno.json doesn't name: add \"@winche/functions\": \"npm:@winche-technologies/functions@^3\" to its imports.",
        ], bundler.MissingPackages(new Dictionary<string, string> { ["index.ts"] = Hello }));
        Assert.Empty(bundler.MissingPackages(new Dictionary<string, string>
        {
            ["index.ts"] = Hello,
            ["deno.json"] = FunctionsSdk.DenoJson,
        }));
        Assert.Contains("\"@winche/functions/testing\": \"npm:@winche-technologies/functions@^3/testing\"", FunctionsSdk.DenoJson);
    }

    [Fact]
    public async Task A_deployment_may_bring_its_own_copies_and_a_function_for_another_protocol_is_refused()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();

        // Its own copies, named in its deno.json (as a project does before the packages are on npm).
        var files = new Dictionary<string, string>
        {
            ["index.ts"] = Hello,
            ["deno.json"] = """{ "imports": { "@winche/functions": "./vendor/functions/dist/index.js", "@winche/admin": "./vendor/admin/dist/index.js", "@winche-technologies/admin": "./vendor/admin/dist/index.js" } }""",
        };
        var root = Path.Combine(AppContext.BaseDirectory, "SdkPackages", "@winche");
        foreach (var name in new[] { "functions", "admin" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, name), "*", SearchOption.AllDirectories))
                files[$"vendor/{name}/{Path.GetRelativePath(Path.Combine(root, name), file).Replace('\\', '/')}"] = File.ReadAllText(file);
        await functions.DeployAsync(files, "Own copies", null);
        Assert.Equal("not there", (await functions.InvokeAsync("hello", null, FunctionCaller.Server)).Result!.GetValue<string>());

        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """export const later = { __winche: 2, trigger: { kind: "callable" }, options: {}, handler: async () => 1 };""",
        }, null, null));
        Assert.Contains("later: written with an @winche/functions for protocol 2; this server speaks 1 (@winche/functions 3.x).", bad.Problems);
    }
}
