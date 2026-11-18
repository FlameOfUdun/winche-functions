using System.Diagnostics;

namespace Winche.Functions.Tests;

/// <summary>Ready when warm; stopping lets runs going on end and refuses new ones.</summary>
[Collection("postgres")]
public class StoppingTests(PostgresFixture pg)
{
    private const string Code = """
        import { callable } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";
        const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
        await sleep(300);   // a slow module to load
        export const slow = callable(async () => { await sleep(1500); return "done"; });
        export const quick = callable(async () => "quick", { minInstances: 2 });
        """;

    [Fact]
    public async Task Warmed_waits_for_kept_instances_and_a_stop_lets_runs_end_and_refuses_new_ones()
    {
        var db = await pg.NewDatabaseAsync();
        await using (var first = await FunctionsApp.StartAsync(db))
            await first.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, "Stop", "admin@x");
        // A new server (a new task) starts with that deployment in force.
        var app = await FunctionsApp.StartAsync(db);
        var functions = app.Service<WincheFunctions>();
        await functions.RefreshAsync();
        await functions.Warmed.WaitAsync(TimeSpan.FromSeconds(30));
        var loaded = (await app.Service<DenoRuntime>().StatsAsync(default)).SelectMany(p => p.Instances).Where(i => i.Function == "quick").ToList();
        Assert.Equal(2, loaded.Count(i => i.State == "idle"));   // loaded, not still loading

        var slow = functions.InvokeAsync("slow", null, FunctionCaller.Server);
        await Task.Delay(300);
        var watch = Stopwatch.StartNew();
        var stopping = app.App.StopAsync();
        await Task.Delay(100);
        var refused = await functions.InvokeAsync("quick", null, FunctionCaller.Server);
        Assert.Equal(("unavailable", "This server is stopping: try again."), (refused.ErrorCode, refused.ErrorMessage));
        var s = await slow;
        Assert.True(s.Ok, s.ErrorMessage);   // it ended on its own
        await stopping;
        Assert.True(watch.ElapsedMilliseconds >= 900, $"stopped after {watch.ElapsedMilliseconds} ms, before the run ended");
        await app.DisposeAsync();
    }

    [Fact]
    public async Task With_nothing_deployed_it_is_warm_at_once()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<WincheFunctions>().RefreshAsync();
        await app.Service<WincheFunctions>().Warmed.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
