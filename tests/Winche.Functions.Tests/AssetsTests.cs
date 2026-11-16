using System.Text;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class AssetsTests(PostgresFixture pg)
{
    private const string Index = """
        import { callable } from "@winche/functions";
        export const read = callable(async (p: { path: string }, ctx) => ctx.assets.readText(p.path));
        export const size = callable(async (p: { path: string }, ctx) => (await ctx.assets.read(p.path))?.length ?? null);
        export const list = callable(async (p: { folder?: string }, ctx) => ({ all: await ctx.assets.list(p?.folder), has: ctx.assets.has("a.txt") }));
        // Only the bundle and the deployment's own assets are readable: not the machine, not other assets.
        export const peek = callable(async (p: { path: string }, ctx) => {
          try { await Deno.readFile(p.path); return "read"; } catch (e) { return (e as Error).name; }
        });
        """;

    private static readonly Dictionary<string, string> Code = new() { ["index.ts"] = Index };

    private static async Task<Dictionary<string, AssetEntry>> PutAsync(WincheFunctions functions, params (string Path, byte[] Content)[] files)
    {
        var map = new Dictionary<string, AssetEntry>();
        foreach (var (path, content) in files)
            map[path] = new AssetEntry(await functions.PutAssetAsync(content, null), content.Length);
        return map;
    }

    [Fact]
    public async Task Functions_read_the_assets_deployed_with_them_and_nothing_else()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var big = new byte[3_000_000];
        new Random(1).NextBytes(big);
        var assets = await PutAsync(functions, ("a.txt", "first"u8.ToArray()), ("fonts/x.ttf", big), ("fonts/sub/y.json", "{\"hi\":1}"u8.ToArray()));

        // Only what isn't stored yet needs sending.
        Assert.Equal(["0000000000000000000000000000000000000000000000000000000000000000"],
            await functions.MissingAssetsAsync([assets["a.txt"].Hash, "0000000000000000000000000000000000000000000000000000000000000000"]));

        var v1 = await functions.DeployAsync(Code, "With assets", "admin@x", assets: assets);
        Assert.Equal(3, v1.AssetFiles.Count);

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

        // A new version with other assets: the old ones aren't readable from it; a rollback brings them back.
        var other = await PutAsync(functions, ("b.txt", "second"u8.ToArray()));
        await functions.DeployAsync(Code, "Other assets", "admin@x", assets: other);
        Assert.Equal("null", await Call("read", new { path = "a.txt" }) ?? "null");
        Assert.Equal("\"second\"", await Call("read", new { path = "b.txt" }));
        var firstFile = Path.Combine(Path.GetTempPath(), "winche-functions", "assets", assets["a.txt"].Hash);
        Assert.Equal("\"NotCapable\"", await Call("peek", new { path = firstFile }));
        await functions.RollBackAsync(v1.Version, null, "admin@x");
        Assert.Equal("\"first\"", await Call("read", new { path = "a.txt" }));
    }

    [Fact]
    public async Task Assets_must_be_sent_whole_before_the_deployment_names_them()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var unsent = new Dictionary<string, AssetEntry> { ["fonts/x.ttf"] = new(new string('a', 64), 10) };
        var e = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(Code, "x", null, assets: unsent));
        Assert.Equal(["assets/fonts/x.ttf"], e.Problems);

        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(Code, "x", null,
            assets: new Dictionary<string, AssetEntry> { ["../x"] = new(new string('a', 64), 1), ["y"] = new("ABC", 1) }));
        Assert.Contains("assets/../x isn't a usable file name (relative, with / between folders).", bad.Problems);
        Assert.Contains("assets/y: the hash is SHA-256 in lowercase hex.", bad.Problems);

        var torn = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.PutAssetAsync(Encoding.UTF8.GetBytes("half"), new string('b', 64)));
        Assert.Contains("doesn't match its hash", torn.Message);
    }
}
