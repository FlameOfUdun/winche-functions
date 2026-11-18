using System.Diagnostics;

namespace Winche.Functions.Tests;

/// <summary>Runs reuse warm workers of their version (unless fresh), and nothing a run leaves behind reaches the next.</summary>
[Collection("postgres")]
public class WarmWorkersTests(PostgresFixture pg)
{
    private const string Code = """
        import { callable } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";

        // Kept at the top of the module: it lasts as long as the worker.
        let runs = 0;
        export const count = callable(async () => ++runs);
        export const countFresh = callable(async () => ++runs, { fresh: true });

        // Returns at once, then writes after it has answered: that write must not happen as part of a later run.
        export const leaky = callable(async (_d, ctx) => {
          setTimeout(() => { admin.documents.doc("leaks/late").set({ by: "a finished run" }).catch(() => {}); }, 300);
          return "answered";
        });

        export const leaked = callable(async (_d, ctx) => (await admin.documents.doc("leaks/late").get()).exists);

        export const slow = callable(async () => { await new Promise((r) => setTimeout(r, 10_000)); return "never"; }, { timeoutSeconds: 1 });
        """;

    [Fact]
    public async Task Runs_reuse_a_warm_worker_with_its_module_state_and_fresh_ones_start_over()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, "Warm", "admin@x");

        int Count(FunctionResult r) { Assert.True(r.Ok, r.ErrorMessage); return r.Result!.GetValue<int>(); }
        var first = Count(await functions.InvokeAsync("count", null, FunctionCaller.Server));
        Assert.Equal(first + 1, Count(await functions.InvokeAsync("count", null, FunctionCaller.Server)));   // the same worker, its state kept
        Assert.Equal(1, Count(await functions.InvokeAsync("countFresh", null, FunctionCaller.Server)));      // a new sandbox every time
        Assert.Equal(1, Count(await functions.InvokeAsync("countFresh", null, FunctionCaller.Server)));

        // Warm runs are quick: well under the time it takes to load the code into a new worker.
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) Count(await functions.InvokeAsync("count", null, FunctionCaller.Server));
        var warm = watch.Elapsed / 20;
        watch.Restart();
        for (var i = 0; i < 5; i++) Count(await functions.InvokeAsync("countFresh", null, FunctionCaller.Server));
        var fresh = watch.Elapsed / 5;
        Assert.True(warm < fresh, $"a warm run took {warm.TotalMilliseconds:0.0} ms, a fresh one {fresh.TotalMilliseconds:0.0} ms");

        // A run that runs out of time takes its worker with it: the next one starts over.
        var slow = await functions.InvokeAsync("slow", null, FunctionCaller.Server);
        Assert.Equal("deadline-exceeded", slow.ErrorCode);
        Assert.True(Count(await functions.InvokeAsync("count", null, FunctionCaller.Server)) >= 1);

        // A new version: new workers, its own state.
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code.Replace("let runs = 0;", "let runs = 100;") }, "Warm again", "admin@x");
        Assert.Equal(101, Count(await functions.InvokeAsync("count", null, FunctionCaller.Server)));
    }

    [Fact]
    public async Task What_a_run_starts_and_leaves_running_cant_act_during_the_next_run()
    {
        var db = await pg.NewDatabaseAsync();
        await using var app = await FunctionsApp.StartAsync(db);
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, "Leaky", "admin@x");

        Assert.Equal("answered", (await functions.InvokeAsync("leaky", null, FunctionCaller.Server)).Result!.GetValue<string>());
        // The worker is back in the pool: the next run is busy while the late write fires.
        var next = functions.InvokeAsync("slow", null, FunctionCaller.Server);
        await Task.Delay(800);
        await next;
        var leaked = await functions.InvokeAsync("leaked", null, FunctionCaller.Server);
        Assert.True(leaked.Ok, leaked.ErrorMessage);
        Assert.False(leaked.Result!.GetValue<bool>());
    }
}
