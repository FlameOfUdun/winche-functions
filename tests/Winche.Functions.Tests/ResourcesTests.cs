using System.Text;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class ResourcesTests(PostgresFixture pg)
{
    private const string Index = """
        import { callable, resources } from "@winche/functions";
        export const read = callable(async (p: { path: string }) => resources.define(p.path).text());
        export const size = callable(async (p: { path: string }) => (await resources.define(p.path).bytes())?.length ?? null);
        export const list = callable(async (p: { folder?: string }) => ({ all: await resources.list(p?.folder), has: resources.define("a.txt").exists() }));
        // Only the bundle and the deployment's own resources are readable: not the machine, not other resources.
        export const peek = callable(async (p: { path: string }, ctx) => {
          try { await Deno.readFile(p.path); return "read"; } catch (e) { return (e as Error).name; }
        });
        """;

    private static readonly Dictionary<string, string> Code = new() { ["index.ts"] = Index };

    private static async Task<Dictionary<string, ResourceEntry>> PutAsync(WincheFunctions functions, params (string Path, byte[] Content)[] files)
    {
        var map = new Dictionary<string, ResourceEntry>();
        foreach (var (path, content) in files)
            map[path] = new ResourceEntry(await functions.PutResourceAsync(content, null), content.Length);
        return map;
    }

    [Fact]
    public async Task Functions_read_the_resources_deployed_with_them_and_nothing_else()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var big = new byte[3_000_000];
        new Random(1).NextBytes(big);
        var resources = await PutAsync(functions, ("a.txt", "first"u8.ToArray()), ("fonts/x.ttf", big), ("fonts/sub/y.json", "{\"hi\":1}"u8.ToArray()));

        // Only what isn't stored yet needs sending.
        Assert.Equal(["0000000000000000000000000000000000000000000000000000000000000000"],
            await functions.MissingResourcesAsync([resources["a.txt"].Hash, "0000000000000000000000000000000000000000000000000000000000000000"]));

        var v1 = await functions.DeployAsync(Code, "With resources", "admin@x", resources: resources);
        Assert.Equal(3, v1.ResourceFiles.Count);

        async Task<string?> Call(string name, object data)
        {
            var r = await functions.InvokeAsync(name, System.Text.Json.JsonSerializer.SerializeToNode(data), FunctionCaller.Server);
            Assert.True(r.Ok, r.ErrorMessage);
            return r.Result?.ToJsonString();
        }
        Assert.Equal("\"first\"", await Call("read", new { path = "a.txt" }));
        Assert.Equal("\"first\"", await Call("read", new { path = "./a.txt" }));
        Assert.Equal("null", await Call("read", new { path = "nope.txt" }) ?? "null");
        Assert.Equal("3000000", await Call("size", new { path = "fonts/x.ttf" }));
        Assert.Equal("""{"all":["fonts/sub/y.json","fonts/x.ttf"],"has":true}""", await Call("list", new { folder = "fonts/" }));
        Assert.Equal("""{"all":["a.txt","fonts/sub/y.json","fonts/x.ttf"],"has":true}""", await Call("list", new { }));
        Assert.Equal("\"NotCapable\"", await Call("peek", new { path = "/etc/hosts" }));

        // A new version with other resources: the old ones aren't readable from it; a rollback brings them back.
        var other = await PutAsync(functions, ("b.txt", "second"u8.ToArray()));
        await functions.DeployAsync(Code, "Other resources", "admin@x", resources: other);
        Assert.Equal("null", await Call("read", new { path = "a.txt" }) ?? "null");
        Assert.Equal("\"second\"", await Call("read", new { path = "b.txt" }));
        var firstFile = Path.Combine(Path.GetTempPath(), "winche-functions", "resources", resources["a.txt"].Hash);
        Assert.Equal("\"NotCapable\"", await Call("peek", new { path = firstFile }));
        await functions.RollBackAsync(v1.Version, null, "admin@x");
        Assert.Equal("\"first\"", await Call("read", new { path = "a.txt" }));
    }

    [Fact]
    public async Task Resources_must_be_sent_whole_before_the_deployment_names_them()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var unsent = new Dictionary<string, ResourceEntry> { ["fonts/x.ttf"] = new(new string('a', 64), 10) };
        var e = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(Code, "x", null, resources: unsent));
        Assert.Equal(["resources/fonts/x.ttf"], e.Problems);

        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(Code, "x", null,
            resources: new Dictionary<string, ResourceEntry> { ["../x"] = new(new string('a', 64), 1), ["y"] = new("ABC", 1) }));
        Assert.Contains("resources/../x isn't a usable file name (relative, with / between folders).", bad.Problems);
        Assert.Contains("resources/y: the hash is SHA-256 in lowercase hex.", bad.Problems);

        var torn = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.PutResourceAsync(Encoding.UTF8.GetBytes("half"), new string('b', 64)));
        Assert.Contains("doesn't match its hash", torn.Message);
    }
}
