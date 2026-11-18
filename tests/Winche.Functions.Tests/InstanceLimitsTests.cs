using System.Text.Json.Nodes;
using Winche.Core;

namespace Winche.Functions.Tests;

/// <summary>A function's memory and instances: its code's, else the project's defaults, with the console's settings over both, within the server's most.</summary>
[Collection("postgres")]
public class InstanceLimitsTests(PostgresFixture pg)
{
    [Fact]
    public async Task Limits_come_from_the_settings_then_the_code_then_the_defaults_and_stay_within_the_server()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var d = await functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, schedule, http } from "@winche/functions"; import * as admin from "@winche/admin";
                export const quick = callable(async () => 1);
                export const report = callable(async () => 1, { memory: 768, maxInstances: 3, reservedInstances: 1, isolate: true });
                export const nightly = schedule("0 2 * * *", async () => {}, { minInstances: 0, maxInstances: 1 });
                export const hook = http.onRequest(async () => "ok", { minInstances: 2 });
                """,
        }, "Limits", "admin@x");
        FunctionLimitsInForce L(string name) => functions.LimitsFor(d.Functions.Single(f => f.Name == name));

        var quick = L("quick");
        Assert.Equal((256, 1, 10, 0, false), (quick.MemoryMb, quick.MinInstances, quick.MaxInstances, quick.ReservedInstances, quick.Isolate));
        Assert.Equal("default", quick.From["memoryMb"]);
        var report = L("report");
        Assert.Equal((768, 1, 3, 1, true), (report.MemoryMb, report.MinInstances, report.MaxInstances, report.ReservedInstances, report.Isolate));
        Assert.Equal(("code", "code", "default"), (report.From["memoryMb"], report.From["isolate"], report.From["minInstances"]));
        Assert.Equal((0, 1), (L("nightly").MinInstances, L("nightly").MaxInstances));   // a schedule keeps none loaded unless it says
        Assert.Equal(2, L("hook").MinInstances);

        // Out of bounds in code: refused when deploying, each with why.
        var refused = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, onDocument } from "@winche/functions"; import * as admin from "@winche/admin";
                export const big = callable(async () => 1, { memory: 4096 });
                export const odd = callable(async () => 1, { maxInstances: 2.5 });
                export const few = callable(async () => 1, { maxInstances: 2, minInstances: 3, reservedInstances: 9 });
                export const many = callable(async () => 1, { maxInstances: 16, reservedInstances: 16 });
                export const check = onDocument.willBeWritten("x/{id}", async () => {}, { memory: 128 });
                """,
        }, null, null));
        Assert.Contains(refused.Problems, p => p == "big: memory is 64 to 1024 MB (this server's most).");
        Assert.Contains(refused.Problems, p => p == "odd: maxInstances is a whole number.");
        Assert.Contains(refused.Problems, p => p == "few: minInstances is 0 to its maxInstances.");
        Assert.Contains(refused.Problems, p => p == "few: reservedInstances is 0 to its maxInstances.");
        Assert.Contains(refused.Problems, p => p.StartsWith("check: a before-function runs inside the write"));
        Assert.Contains(refused.Problems, p => p == "Functions reserve 25 runs at once in all; this server allows 16.");

        // The console: project defaults, and report's own limits over its code's.
        var area = app.Service<SettingsArea<FunctionsSettings>>();
        Assert.Contains(area.Validate(JsonNode.Parse("""{ "defaultMemoryMb": 32, "limits": { "report": { "maxInstances": 0 } } }""")!),
            p => p == "The default memory is at least 64 MB.");
        Assert.Empty(area.Validate(JsonNode.Parse("""{ "defaultMemoryMb": 2048, "defaultMaxInstances": 50 }""")!));   // held to the server's limits
        Assert.Contains(area.Validate(JsonNode.Parse("""{ "limits": { "report": { "maxInstances": 0 } } }""")!), p => p.StartsWith("report: maxInstances is 1 to 16"));
        await app.Service<SettingsStore>().SaveAsync(FunctionsSettings.Area,
            JsonNode.Parse("""{ "defaultMemoryMb": 128, "defaultMaxInstances": 4, "limits": { "report": { "memoryMb": 512, "isolate": false } } }""")!, "admin@x", "Limits", null);
        for (var i = 0; i < 100 && area.Current.DefaultMemoryMb != 128; i++) await Task.Delay(100);
        Assert.Equal((128, 4), (L("quick").MemoryMb, L("quick").MaxInstances));
        report = L("report");
        Assert.Equal((512, false, 3), (report.MemoryMb, report.Isolate, report.MaxInstances));
        Assert.Equal(("setting", "setting", "code"), (report.From["memoryMb"], report.From["isolate"], report.From["maxInstances"]));
    }
}
