namespace Winche.Functions.Tests;

/// <summary>Processes: an isolated function's runaway memory stops only its own process; in the shared one, its neighbours' runs in flight fail and the next ones work.</summary>
[Collection("postgres")]
public class ProcessesTests(PostgresFixture pg)
{
    private const string Code = """
        import { callable } from "@winche/functions"; import * as admin from "@winche/admin";
        const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

        // Keeps everything until the heap gives out.
        const hog = () => { const keep = []; for (;;) keep.push(Array.from({ length: 1_000_000 }, (_, i) => i + 0.5)); };
        export const runaway = callable(async () => hog(), { isolate: true, memory: 64 });
        export const sharedRunaway = callable(async () => hog());

        export const slow = callable(async () => { await sleep(1500); return "slow"; });
        // In flight long enough for any runaway to fill its heap first, however slow the machine.
        export const long = callable(async () => { await sleep(20000); return "long"; }, { timeoutSeconds: 30 });
        export const quick = callable(async () => "quick");
        """;

    private static async Task<(FunctionsApp App, WincheFunctions Functions)> Start(PostgresFixture pg, Action<FunctionsOptions>? more = null)
    {
        var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), o =>
        {
            o.SharedProcessMemoryMb = 320;
            o.MemoryBudget = new FunctionsMemoryBudget(4096);
            more?.Invoke(o);
        });
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, "Processes", "admin@x");
        return (app, functions);
    }

    [Fact]
    public async Task A_runaway_in_its_own_process_fails_alone_and_others_keep_running()
    {
        var (app, functions) = await Start(pg);
        await using var _ = app;
        var slow = functions.InvokeAsync("slow", null, FunctionCaller.Server);
        await Task.Delay(200);

        var runaway = await functions.InvokeAsync("runaway", null, FunctionCaller.Server);
        Assert.False(runaway.Ok);
        Assert.Equal("resource-exhausted", runaway.ErrorCode);
        Assert.Contains("runaway's process", runaway.ErrorMessage);

        var s = await slow;
        Assert.True(s.Ok, s.ErrorMessage);   // the shared process never noticed
        Assert.Equal("quick", (await functions.InvokeAsync("quick", null, FunctionCaller.Server)).Result!.GetValue<string>());
        // Its process starts again for the next run (which runs out again, alone).
        Assert.Equal("resource-exhausted", (await functions.InvokeAsync("runaway", null, FunctionCaller.Server)).ErrorCode);
    }

    [Fact]
    public async Task A_runaway_in_the_shared_process_fails_the_runs_in_flight_there_and_the_next_ones_work()
    {
        var (app, functions) = await Start(pg);
        await using var _ = app;
        Assert.True((await functions.InvokeAsync("quick", null, FunctionCaller.Server)).Ok);
        var slow = functions.InvokeAsync("long", null, FunctionCaller.Server);
        await Task.Delay(200);

        var runaway = await functions.InvokeAsync("sharedRunaway", null, FunctionCaller.Server);
        Assert.Equal("resource-exhausted", runaway.ErrorCode);
        Assert.Contains("the shared functions process", runaway.ErrorMessage);
        Assert.Equal("resource-exhausted", (await slow).ErrorCode);   // in the same process: stopped with it

        var after = await functions.InvokeAsync("quick", null, FunctionCaller.Server);
        Assert.True(after.Ok, after.ErrorMessage);
    }

    [Fact]
    public async Task An_isolated_function_that_doesnt_fit_the_budget_runs_in_the_shared_process()
    {
        // 320 MB for the shared process leaves no room for runaway's 64 + 128.
        var (app, functions) = await Start(pg, o => o.MemoryBudget = new FunctionsMemoryBudget(400));
        await using var _ = app;
        var slow = functions.InvokeAsync("long", null, FunctionCaller.Server);
        await Task.Delay(200);
        var runaway = await functions.InvokeAsync("runaway", null, FunctionCaller.Server);
        Assert.Contains("the shared functions process", runaway.ErrorMessage);
        Assert.Equal("resource-exhausted", (await slow).ErrorCode);
        Assert.True(app.Service<DenoRuntime>().Refused("runaway"));
    }
}
