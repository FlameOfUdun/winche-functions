using System.Diagnostics;

namespace Winche.Functions.Tests;

/// <summary>Instances enforced: memory (replaced when over), at most maxInstances at once, reserved slots kept free.</summary>
[Collection("postgres")]
public class InstancesTests(PostgresFixture pg)
{
    private const string Code = """
        import { callable } from "@winche/functions"; import * as admin from "@winche/admin";
        const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

        // Keeps about 20 MB more on every run (numbers in arrays: on the heap).
        const kept = [];
        export const grow = callable(async () => { kept.push(Array.from({ length: 2_600_000 }, (_, i) => i + 0.5)); return kept.length; }, { memory: 64 });

        // Makes about 100 MB of garbage on every run, and keeps nothing.
        let runs = 0;
        export const churn = callable(async () => { for (let i = 0; i < 5; i++) Array.from({ length: 2_600_000 }, (_, i) => i + 0.5); return ++runs; }, { memory: 64 });

        // One at a time: what it sees running at once is never more than 1.
        let active = 0, peak = 0;
        export const single = callable(async () => { active++; peak = Math.max(peak, active); await sleep(300); active--; return peak; }, { maxInstances: 1 });

        export const queue = callable(async () => { await sleep(900); return "done"; }, { maxInstances: 1, timeoutSeconds: 1 });

        export const greedy = callable(async () => { await sleep(1500); return "greedy"; }, { maxInstances: 16 });
        export const vip = callable(async () => "vip", { reservedInstances: 1 });
        """;

    private static int Number(FunctionResult r) { Assert.True(r.Ok, r.ErrorMessage); return r.Result!.GetValue<int>(); }

    [Fact]
    public async Task An_instance_holding_more_than_its_memory_after_a_run_is_replaced_and_garbage_doesnt_count()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, "Instances", "admin@x");

        var seen = new List<int>();
        for (var i = 0; i < 6; i++) seen.Add(Number(await functions.InvokeAsync("grow", null, FunctionCaller.Server)));
        // It grows until it holds more than 64 MB, then a new instance starts from nothing.
        Assert.Contains(1, seen.Skip(1));
        Assert.True(seen.Max() is >= 2 and <= 4, string.Join(",", seen));

        // Lots of garbage, nothing kept: collected, never over, the same instance throughout.
        var churned = new List<int>();
        for (var i = 0; i < 4; i++) churned.Add(Number(await functions.InvokeAsync("churn", null, FunctionCaller.Server)));
        Assert.Equal([1, 2, 3, 4], churned);
    }

    [Fact]
    public async Task At_most_maxInstances_run_at_once_and_one_that_cant_get_a_turn_within_its_time_fails()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, "Instances", "admin@x");

        var watch = Stopwatch.StartNew();
        var peaks = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => functions.InvokeAsync("single", null, FunctionCaller.Server)));
        Assert.All(peaks, p => Assert.Equal(1, Number(p)));
        Assert.True(watch.ElapsedMilliseconds >= 850, $"took {watch.ElapsedMilliseconds} ms: they ran together");

        // One at a time, a second each: of three started together, one can't get its turn within its second.
        var queued = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => functions.InvokeAsync("queue", null, FunctionCaller.Server)));
        var busy = Assert.Single(queued, r => !r.Ok);
        Assert.Equal("resource-exhausted", busy.ErrorCode);
        Assert.Contains("queue is busy", busy.ErrorMessage);
    }

    [Fact]
    public async Task A_function_with_a_reserved_instance_runs_while_others_hold_every_shared_one()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, "Instances", "admin@x");

        // 16 at once on this server, 1 reserved for vip: 16 greedy runs take the 15 shared slots, and one of them waits.
        var greedy = Enumerable.Range(0, 16).Select(_ => functions.InvokeAsync("greedy", null, FunctionCaller.Server)).ToList();
        await Task.Delay(400);
        var watch = Stopwatch.StartNew();
        var vip = await functions.InvokeAsync("vip", null, FunctionCaller.Server);
        Assert.True(vip.Ok, vip.ErrorMessage);
        Assert.True(watch.ElapsedMilliseconds < 800, $"vip waited {watch.ElapsedMilliseconds} ms for the greedy ones");
        Assert.All(await Task.WhenAll(greedy), r => Assert.True(r.Ok, r.ErrorMessage));
    }
}
