using System.Text;
using System.Text.Json;
using Winche.Assets;
using Winche.Database.Runtime;

namespace Winche.Functions.Tests;

/// <summary>admin.assets (channels, releases, files with the server's rights) and the assets triggers.</summary>
[Collection("postgres")]
public class AssetsTests(PostgresFixture pg)
{
    private const string Index = """
        import { onAssets, callable } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";
        export const strings = callable(async (p: { channel: string; lang: string }, ctx) => {
          const release = await admin.assets.channel(p.channel).get();
          if (!release) return null;
          return { release: release.number, has: release.has(`translations/${p.lang}.json`), paths: release.paths("translations"),
            strings: await release.file(`translations/${p.lang}.json`).json() };
        });
        export const missing = callable(async (_: unknown, ctx) => {
          try { await (await admin.assets.release(1))!.file("nope.txt").text(); return "read"; } catch (e) { return (e as { code?: string }).code; }
        });
        export const overview = callable(async (_: unknown, ctx) => ({
          channels: (await admin.assets.channels()).map((c) => `${c.name}@${c.release}`),
          releases: (await admin.assets.releases()).map((r) => r.number),
          link: (await (await admin.assets.release(1))!.file("translations/en.json").downloadUrl()).url.startsWith("mem://"),
        }));
        export const onRelease = onAssets.released(async (e, ctx) => {
          await admin.documents.doc(`releases/${e.release}`).set({ channel: e.channel, note: e.note, by: e.by });
        });
        export const onBeta = onAssets.channelMoved(async (e, ctx) => {
          await admin.documents.doc(`moves/${e.channel}-${e.release}`).set({ at: e.at });
        }, { channel: "beta" });
        """;

    private static async Task PublishAsync(WincheAssets assets, Dictionary<string, string> files, string? channel, string note)
    {
        var map = new Dictionary<string, AssetFile>();
        foreach (var (path, text) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = AssetPaths.Hash(bytes);
            await assets.PutContentAsync(new MemoryStream(bytes), hash);
            map[path] = new AssetFile(hash, bytes.Length, "");
        }
        await assets.PublishAsync(new AssetPublishRequest(map, note, channel), "admin@x");
    }

    [Fact]
    public async Task Functions_read_assets_and_run_when_they_change()
    {
        if (Deno.Path is null) return;
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var deployed = await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Index }, "Assets", "admin@x");
        Assert.Contains(deployed.Functions, f => f.Name == "onRelease" && f.Trigger.Describe() == "assets released");
        await Task.Delay(500);   // the triggers become hooks

        var assets = app.Service<WincheAssets>();
        await PublishAsync(assets, new() { ["translations/en.json"] = "{\"hi\":\"Hi\"}", ["translations/de.json"] = "{\"hi\":\"Hallo\"}" }, "production", "First");

        async Task<JsonElement> Call(string name, object data)
        {
            var r = await functions.InvokeAsync(name, JsonSerializer.SerializeToNode(data), FunctionCaller.Server);
            Assert.True(r.Ok, r.ErrorMessage);
            return JsonDocument.Parse(r.Result?.ToJsonString() ?? "null").RootElement.Clone();
        }
        var strings = await Call("strings", new { channel = "production", lang = "de" });
        Assert.Equal(1, strings.GetProperty("release").GetInt32());
        Assert.Equal("Hallo", strings.GetProperty("strings").GetProperty("hi").GetString());
        Assert.Equal(2, strings.GetProperty("paths").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, (await Call("strings", new { channel = "nope", lang = "de" })).ValueKind);
        Assert.Equal("not-found", (await Call("missing", new { })).GetString());
        var overview = await Call("overview", new { });
        Assert.Equal("production@1", overview.GetProperty("channels")[0].GetString());
        Assert.True(overview.GetProperty("link").GetBoolean());

        // The triggers: every release; channel moves of beta only.
        await assets.MoveChannelAsync("beta", 1, "admin@x");
        await assets.MoveChannelAsync("other", 1, "admin@x");
        var db = app.Service<DocumentDatabase>();
        for (var i = 0; i < 100 && (await db.GetAsync("releases/1") is null || await db.GetAsync("moves/beta-1") is null); i++) await Task.Delay(100);
        var released = await db.GetAsync("releases/1");
        Assert.NotNull(released);
        Assert.Equal(new Winche.Values.StringValue("production"), released!.Fields["channel"]);
        Assert.Equal(new Winche.Values.StringValue("First"), released.Fields["note"]);
        Assert.NotNull(await db.GetAsync("moves/beta-1"));
        Assert.Null(await db.GetAsync("moves/other-1"));
    }
}
