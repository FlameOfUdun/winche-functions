using System.Text.Json;
using Winche.Database.Runtime;
using Xunit;

namespace Winche.Functions.Tests;

/// <summary>Functions and Winche Realtime: ctx-side lists, channels and presence with the server's rights, and onRealtime.closed.</summary>
[Collection("postgres")]
public class RealtimeTests(PostgresFixture pg)
{
    private const string Index = """
        import { onRealtime, callable } from "@winche-technologies/winche-functions";
        import { realtime, documents } from "@winche-technologies/winche-admin";
        export const note = callable(async () => realtime.list("sessions/s1").append([{ key: "server:1", payload: { n: 1 } }]));
        export const peek = callable(async () => {
          const r = await realtime.list("sessions/s1").read();
          return { keys: r.items.map((i) => i.key), count: r.list?.count ?? null, first: r.items[0]?.json() ?? null, by: r.items[0]?.by ?? null };
        });
        export const shout = callable(async () => { await realtime.channel("sessions/s1", "alerts").publish({ level: "high" }); return (await realtime.presence("sessions/s1").members()).length; });
        export const shut = callable(async () => (await realtime.list("sessions/s1").close()).state);
        export const outcome = onRealtime.closed("sessions/{session}", async (e) => {
          await documents.doc(`outcomes/${e.params.session}`).set({ items: e.list.count, path: e.path });
        });
        """;

    [Fact]
    public async Task Functions_use_realtime_lists_and_run_when_one_closes()
    {
        if (Deno.Path is null) return;
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), realtime: true);
        var functions = app.Service<WincheFunctions>();
        var deployed = await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = Index }, "Realtime", "admin@x");
        Assert.Contains(deployed.Functions, f => f.Name == "outcome" && f.Trigger.Kind == "realtime");
        await Task.Delay(500);   // the trigger becomes a hook

        async Task<JsonElement> Call(string name)
        {
            var r = await functions.InvokeAsync(name, null, FunctionCaller.Server);
            Assert.True(r.Ok, r.ErrorMessage);
            return JsonDocument.Parse(r.Result?.ToJsonString() ?? "null").RootElement.Clone();
        }
        Assert.Equal(1, (await Call("note"))[0].GetInt32());
        var peek = await Call("peek");
        Assert.Equal("server:1", peek.GetProperty("keys")[0].GetString());
        Assert.Equal(1, peek.GetProperty("first").GetProperty("n").GetInt32());
        Assert.Equal(0, (await Call("shout")).GetInt32());
        Assert.Equal("closed", (await Call("shut")).GetString());

        var db = app.Service<DocumentDatabase>();
        for (var i = 0; i < 100 && await db.GetAsync("outcomes/s1") is null; i++) await Task.Delay(100);
        var outcome = await db.GetAsync("outcomes/s1");
        Assert.NotNull(outcome);
        Assert.Equal(new Winche.Values.IntegerValue(1), outcome!.Fields["items"]);
    }

    [Fact]
    public async Task Realtime_triggers_are_for_closed_lists()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), realtime: true);
        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string> { ["index.ts"] = """
            export const odd = { __winche: 1, trigger: { kind: "realtime", events: ["opened"] }, options: {}, run: () => null, handler: () => null };
            """ }, null, null));
        Assert.Contains(bad.Problems, p => p.Contains("realtime events are closed"));
    }
}
