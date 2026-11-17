using System.Text.Json.Nodes;
using Winche.Core;

namespace Winche.Functions.Tests;

/// <summary>What the console's Instances view reads: instances now, what happened to them, and the server's limits as they change.</summary>
[Collection("postgres")]
public class InstancesViewTests(PostgresFixture pg)
{
    private const string Code = """
        import { callable } from "@winche/functions";
        const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
        const kept = [];
        export const grow = callable(async () => { kept.push(Array.from({ length: 2_600_000 }, (_, i) => i + 0.5)); return kept.length; }, { memory: 64 });
        export const slow = callable(async () => { await sleep(1500); return "slow"; }, { timeoutSeconds: 1, minInstances: 2 });
        export const runaway = callable(async () => { const k = []; for (;;) k.push(Array.from({ length: 1_000_000 }, (_, i) => i + 0.5)); }, { isolate: true, memory: 64 });
        export const wait = callable(async () => { await sleep(600); return 1; }, { maxInstances: 16 });
        """;

    [Fact]
    public async Task Instances_now_and_what_happened_to_them()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), o => o.MemoryBudget = new FunctionsMemoryBudget(8192));
        var functions = app.Service<WincheFunctions>();
        var runtime = app.Service<DenoRuntime>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, "View", "admin@x");

        // slow keeps 2 loaded (and the callables' default 1 each for the others).
        IReadOnlyList<FunctionInstanceInfo> Of(IReadOnlyList<FunctionProcessInfo> ps, string f) => ps.SelectMany(p => p.Instances).Where(i => i.Function == f).ToList();
        IReadOnlyList<FunctionProcessInfo> now = [];
        for (var i = 0; i < 50 && Of(now, "slow").Count(x => x.State == "idle") < 2; i++) { await Task.Delay(100); now = await runtime.StatsAsync(default); }
        Assert.Equal(2, Of(now, "slow").Count(x => x.State == "idle"));
        var shared = Assert.Single(now, p => p.Function is null);
        Assert.True(shared.Running);
        Assert.Equal(1024, shared.HeapMb);

        for (var i = 0; i < 6; i++) await functions.InvokeAsync("grow", null, FunctionCaller.Server);
        Assert.Equal("deadline-exceeded", (await functions.InvokeAsync("slow", null, FunctionCaller.Server)).ErrorCode);
        Assert.Equal("resource-exhausted", (await functions.InvokeAsync("runaway", null, FunctionCaller.Server)).ErrorCode);

        var events = runtime.Events;
        Assert.Contains(events, e => e is { Function: "grow", Kind: "memory", Process: null } && e.Message.Contains("over its 64 MB"));
        Assert.Contains(events, e => e is { Function: "slow", Kind: "timeout" });
        Assert.Contains(events, e => e is { Function: "runaway", Kind: "out-of-memory", Process: "runaway" });
        Assert.DoesNotContain(events, e => e.Function == "runaway" && e.Kind == "crash");

        // A busy one shows as busy, with its heap after its last run on the others.
        var busy = functions.InvokeAsync("wait", null, FunctionCaller.Server);
        await Task.Delay(200);
        now = await runtime.StatsAsync(default);
        Assert.Contains(Of(now, "wait"), x => x.State == "busy");
        Assert.All(Of(now, "grow"), x => Assert.True(x.HeapMb is > 0, "heap after its run"));
        await busy;
        Assert.Contains(now, p => p.Function == "runaway");   // its process (stopped or started again) is listed
    }

    [Fact]
    public async Task Settings_asking_for_a_process_that_can_never_fit_the_budget_are_refused_and_the_servers_limit_follows_changes()
    {
        FunctionsOptions? options = null;
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), o =>
        {
            o.MemoryBudget = new FunctionsMemoryBudget(1500);
            o.SharedProcessMemoryMb = 1024;
            o.MaxConcurrency = 2;
            options = o;
        });
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code.Replace(", { maxInstances: 16 }", "") }, "View", "admin@x");

        var area = app.Service<SettingsArea<FunctionsSettings>>();
        Assert.Contains(area.Validate(JsonNode.Parse("""{ "limits": { "grow": { "memoryMb": 512, "isolate": true } } }""")!),
            p => p == "grow: 512 MB in a process of its own doesn't fit this server's functions memory (1500 MB, 1024 MB of it for the shared process): at most 348 MB.");
        Assert.Empty(area.Validate(JsonNode.Parse("""{ "limits": { "grow": { "memoryMb": 300, "isolate": true } } }""")!));
        Assert.Empty(area.Validate(JsonNode.Parse("""{ "limits": { "grow": { "memoryMb": 512 } } }""")!));   // in the shared process: no budget asked

        // 2 at once: the third waits; raised to 4 while running, all four go together.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => functions.InvokeAsync("wait", null, FunctionCaller.Server)));
        Assert.True(watch.ElapsedMilliseconds >= 1100, $"{watch.ElapsedMilliseconds} ms");
        options!.MaxConcurrency = 4;
        watch.Restart();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => functions.InvokeAsync("wait", null, FunctionCaller.Server)));
        Assert.True(watch.ElapsedMilliseconds < 1100, $"{watch.ElapsedMilliseconds} ms");
    }
}
