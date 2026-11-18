using System.Text.Json.Nodes;
using Winche.Core;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class TimeoutSettingsTests(PostgresFixture pg)
{
    [Fact]
    public async Task A_functions_time_is_its_own_setting_else_its_codes_else_the_default_and_never_past_the_servers_longest()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var d = await functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, schedule, onDocument } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";
                export const quick = callable(async () => 1);
                export const report = callable(async () => 1, { timeoutSeconds: 300 });
                export const spin = callable(async () => { while (true) {} }, { timeoutSeconds: 600 });
                export const nightly = schedule("0 2 * * *", async () => {}, { timeoutSeconds: 1200 });
                export const check = onDocument.willBeWritten("x/{id}", async () => {}, { timeoutSeconds: 10 });
                """,
        }, "Times", "admin@x");
        int Time(string name) => functions.TimeoutFor(d.Functions.Single(f => f.Name == name));
        Assert.Equal((60, 300, 600, 1200, 10), (Time("quick"), Time("report"), Time("spin"), Time("nightly"), Time("check")));

        // Too long for this server: refused at deploy.
        var tooLong = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """import { callable } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin"; export const x = callable(async () => 1, { timeoutSeconds: 1201 });""",
        }, null, null));
        Assert.Contains(tooLong.Problems, p => p.StartsWith("x: timeoutSeconds is 1 to 1200"));

        // The console: a new default, and spin's own time, over its code's.
        var area = app.Service<SettingsArea<FunctionsSettings>>();
        Assert.Contains(area.Validate(JsonNode.Parse("""{ "defaultTimeoutSeconds": 90, "timeouts": { "spin": 1201 } }""")!), p => p.StartsWith("spin: 1 second to 20 minutes"));
        await app.Service<SettingsStore>().SaveAsync(FunctionsSettings.Area, JsonNode.Parse("""{ "defaultTimeoutSeconds": 90, "timeouts": { "spin": 1, "report": 30 } }""")!, "admin@x", "Times", null);
        for (var i = 0; i < 100 && area.Current.DefaultTimeoutSeconds != 90; i++) await Task.Delay(100);
        Assert.Equal((90, 30, 1, 1200, 10), (Time("quick"), Time("report"), Time("spin"), Time("nightly"), Time("check")));

        // And it's what a run gets: spin is stopped after its own 1 second, not its code's 10 minutes.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var spun = await functions.InvokeAsync("spin", null, FunctionCaller.Server);
        Assert.Equal("deadline-exceeded", spun.ErrorCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }
}
