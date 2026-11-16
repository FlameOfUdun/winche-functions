using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Auth;
using Winche.Core;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Values;
using Winche.Storage.Services;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class FunctionsTests(PostgresFixture pg)
{
    private static Dictionary<string, string> Code(string index) => new() { ["index.ts"] = index };

    private static async Task Until(Func<Task<bool>> condition, string what, int seconds = 20)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    private static async Task<(HttpStatusCode Status, JsonNode Body)> Call(FunctionsApp app, string name, object? data, string? uid = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{app.Url}/functions/v1/call/{name}") { Content = JsonContent.Create(new { data }) };
        if (uid is not null) request.Headers.Add("X-Test-Uid", uid);
        using var response = await app.Http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task Deploys_that_cant_work_say_why()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();

        var noEntry = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(new Dictionary<string, string> { ["lib.ts"] = "export const x = 1;" }, null, null));
        Assert.Contains(noEntry.Problems, p => p.Contains("index.ts"));

        var broken = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(Code("export const = ;"), null, null));
        Assert.Equal("The code doesn't build.", broken.Message);

        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(Code("""
            import { documents, schedule, callable } from "@winche/functions";
            export const a = documents.onCreated("patients/{id", async () => {});
            export const b = schedule("every day", async () => {});
            export const c = callable(async () => 1, { timeoutSeconds: 9999 });
            """), null, null));
        Assert.Contains(bad.Problems, p => p.StartsWith("a:"));
        Assert.Contains(bad.Problems, p => p.StartsWith("b: 'every day' isn't a cron schedule"));
        Assert.Contains(bad.Problems, p => p.StartsWith("c: timeoutSeconds is 1 to 540"));
        Assert.Null(functions.Current);
    }

    [Fact]
    public async Task Callables_get_the_caller_reach_the_database_and_report_errors()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<DocumentDatabase>().WriteAsync([new SetWrite { Path = "people/ann", Fields = new Dictionary<string, Value> { ["first"] = new StringValue("Ann"), ["age"] = new IntegerValue(41) } }]);
        var d = await app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, HttpsError } from "@winche/functions";
                import { greet } from "./lib/greet.ts";
                export const hello = callable(async (data: { name: string }, ctx) => {
                  const person = await ctx.db.get(`people/${data.name}`);
                  if (!person) throw new HttpsError("not-found", `There's no ${data.name}.`);
                  await ctx.db.set(`visits/${data.name}`, { at: new Date("2026-11-12T10:00:00Z"), by: ctx.auth?.uid ?? null });
                  const visits = await ctx.db.query("visits", { where: [["by", "==", ctx.auth?.uid ?? null]] });
                  console.log("greeted", data.name);
                  return { greeting: greet(String(person.fields.first)), age: person.fields.age, role: ctx.auth?.token.role, visits: visits.length };
                });
                export const sneaky = callable(async () => await Deno.readTextFile("/etc/hosts"));
                export const slow = callable(async () => { while (true) {} }, { timeoutSeconds: 1 });
                export const picky = callable(async () => { throw new HttpsError("failed-precondition", "Not yet.", { retryAfter: 3 }); });
                """,
            ["lib/greet.ts"] = "export const greet = (name: string): string => `Hello ${name}`;",
        }, "First", "admin@x");
        Assert.Equal(1, d.Version);
        Assert.Equal(["hello", "picky", "slow", "sneaky"], d.Functions.Select(f => f.Name).Order());
        Assert.Equal(1, d.Functions.Single(f => f.Name == "slow").TimeoutSeconds);

        var (status, body) = await Call(app, "hello", new { name = "ann" }, uid: "u1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("Hello Ann", 41, "clinician", 1), (body["data"]!["greeting"]!.GetValue<string>(), body["data"]!["age"]!.GetValue<int>(),
            body["data"]!["role"]!.GetValue<string>(), body["data"]!["visits"]!.GetValue<int>()));
        var visit = await app.Service<DocumentDatabase>().GetAsync("visits/ann");
        Assert.IsType<TimestampValue>(visit!.Fields["at"]);   // a Date became a timestamp

        (status, body) = await Call(app, "hello", new { name = "bob" });
        Assert.Equal((HttpStatusCode.NotFound, "There's no bob."), (status, body["error"]!["message"]!.GetValue<string>()));

        Assert.Equal("not-found", body["error"]!["code"]!.GetValue<string>());

        (status, body) = await Call(app, "picky", null);   // an HttpsError's details reach the app
        Assert.Equal((HttpStatusCode.BadRequest, "failed-precondition", 3), (status, body["error"]!["code"]!.GetValue<string>(),
            body["error"]!["details"]!["retryAfter"]!.GetValue<int>()));

        (status, body) = await Call(app, "sneaky", null);
        Assert.Equal((HttpStatusCode.InternalServerError, "The function failed."), (status, body["error"]!["message"]!.GetValue<string>()));   // details stay in the logs

        (status, body) = await Call(app, "slow", null);
        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(app, "nope", null)).Status);

        var direct = await app.Service<WincheFunctions>().InvokeAsync("sneaky", null, FunctionCaller.Server);
        Assert.Contains("read access", direct.ErrorMessage);
    }

    [Fact]
    public async Task Document_file_and_user_triggers_run_with_their_events()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<WincheFunctions>().DeployAsync(Code("""
            import { documents, files, users } from "@winche/functions";
            export const onVisit = documents.onCreated("patients/{patientId}/visits/{visitId}", async (e, ctx) => {
              await ctx.db.set(`audit/${e.params.visitId}`, { patient: e.params.patientId, ward: e.data?.fields.ward ?? null, type: e.type });
            });
            export const onUpload = files.onUploaded("scans/{rest=**}", async (e, ctx) => {
              await ctx.db.set("audit/upload", { path: e.path, rest: e.params.rest, mime: e.file?.mimeType ?? null });
            });
            export const welcome = users.onCreated(async (e, ctx) => {
              await ctx.users.setClaims(e.uid, { role: "newcomer", contact: e.user.email });
            });
            """), null, null);
        var db = app.Service<DocumentDatabase>();
        await db.WriteAsync([new SetWrite { Path = "patients/p1/visits/v7", Fields = new Dictionary<string, Value> { ["ward"] = new StringValue("A3") } }]);
        await Until(async () => await db.GetAsync("audit/v7") is not null, "the document trigger");
        var audit = (await db.GetAsync("audit/v7"))!;
        Assert.Equal(("p1", "A3", "created"), (((StringValue)audit.Fields["patient"]).Value, ((StringValue)audit.Fields["ward"]).Value, ((StringValue)audit.Fields["type"]).Value));

        var files = app.Service<FileStorage>();
        await files.SetAsync("scans/2026/p1.pdf", "application/pdf", 3);
        app.Archive.Objects[(await files.GenerateUploadUrlAsync("scans/2026/p1.pdf")).Url["mem://".Length..]] = 3;   // uploaded to its key
        await files.ConfirmUploadAsync("scans/2026/p1.pdf");
        await Until(async () => await db.GetAsync("audit/upload") is not null, "the file trigger");
        Assert.Equal("2026/p1.pdf", ((StringValue)(await db.GetAsync("audit/upload"))!.Fields["rest"]).Value);

        WincheUserInfo ann;
        await using (var scope = app.App.Services.CreateAsyncScope())
            ann = await scope.ServiceProvider.GetRequiredService<IWincheAuth>().CreateUserAsync("ann@example.com", "correct horse battery");
        async Task<WincheUserInfo?> User()
        {
            await using var scope = app.App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IWincheAuth>().GetUserAsync(ann.Uid);
        }
        await Until(async () => (await User())?.CustomClaims.GetValueOrDefault("role") as string == "newcomer", "the user trigger");
        Assert.Equal("ann@example.com", (await User())!.CustomClaims["contact"]);

        var deliveries = await app.Service<Winche.Webhooks.HookOutbox>().ListAsync("fn.onVisit");
        Assert.Equal("delivered", Assert.Single(deliveries).Status);   // listed with the other hooks' deliveries
    }

    [Fact]
    public async Task Disabling_and_deleting_a_user_run_their_triggers_with_who_it_was()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<WincheFunctions>().DeployAsync(Code("""
            import { users } from "@winche/functions";
            export const offboard = users.onDisabled(async (e, ctx) => { await ctx.db.set(`offboarded/${e.uid}`, { email: e.user.email }); });
            export const erase = users.onDeleted(async (e, ctx) => {
              await ctx.db.set(`erased/${e.uid}`, { email: e.user.email });
              await ctx.db.delete(`people/${e.uid}`, { recursive: true });
            });
            """), null, null);
        var db = app.Service<DocumentDatabase>();
        WincheUserInfo ann;
        await using (var scope = app.App.Services.CreateAsyncScope())
            ann = await scope.ServiceProvider.GetRequiredService<IWincheAuth>().CreateUserAsync("ann@example.com", "correct horse battery");
        await db.WriteAsync([new SetWrite { Path = $"people/{ann.Uid}/notes/n1", Fields = new Dictionary<string, Value> { ["text"] = new StringValue("hi") } }]);

        await using (var scope = app.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IWincheAuth>().SetDisabledAsync(ann.Uid, true);
        await Until(async () => await db.GetAsync($"offboarded/{ann.Uid}") is not null, "users.onDisabled");
        Assert.Equal("ann@example.com", ((StringValue)(await db.GetAsync($"offboarded/{ann.Uid}"))!.Fields["email"]).Value);

        await using (var scope = app.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IWincheAuth>().DeleteUserAsync(ann.Uid);
        await Until(async () => await db.GetAsync($"erased/{ann.Uid}") is not null, "users.onDeleted");
        Assert.Equal("ann@example.com", ((StringValue)(await db.GetAsync($"erased/{ann.Uid}"))!.Fields["email"]).Value);   // as they were
        Assert.Null(await db.GetAsync($"people/{ann.Uid}/notes/n1"));
    }

    [Fact]
    public async Task Schedules_run_once_when_due_and_failures_are_retried()
    {
        var db = await pg.NewDatabaseAsync();
        await using var app = await FunctionsApp.StartAsync(db);
        await app.Service<WincheFunctions>().DeployAsync(Code("""
            import { schedule } from "@winche/functions";
            let tries = 0;
            export const tidy = schedule("*/5 * * * *", async (e, ctx) => {
              const runs = (await ctx.db.get("state/tidy"))?.fields.runs as number ?? 0;
              if (runs === 0 && (await ctx.db.get("state/failed")) === null) {
                await ctx.db.set("state/failed", { once: true });
                throw new Error("first try fails");
              }
              await ctx.db.set("state/tidy", { runs: runs + 1, scheduledAt: e.scheduledAt });
            });
            """), null, null);
        var scheduler = app.Service<FunctionScheduler>();
        await scheduler.RunDueAsync(default);   // first look: starts counting from now

        await using (var conn = new NpgsqlConnection(db))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE winche_function_schedules SET last_due = now() - interval '17 minutes'", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await scheduler.RunDueAsync(default);   // three times were missed: one run
        await scheduler.RunDueAsync(default);   // nothing more is due
        var documents = app.Service<DocumentDatabase>();
        await Until(async () => await documents.GetAsync("state/tidy") is not null, "the scheduled run (after its retry)");
        await Task.Delay(1000);
        Assert.Equal(1L, ((IntegerValue)(await documents.GetAsync("state/tidy"))!.Fields["runs"]).Value);
        var delivery = Assert.Single(await app.Service<Winche.Webhooks.HookOutbox>().ListAsync("fn.tidy"));
        Assert.Equal((2, "delivered"), (delivery.Attempts, delivery.Status));
    }

    [Fact]
    public async Task Versions_are_kept_and_rolling_back_deploys_the_old_code_again()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(Code("""import { callable } from "@winche/functions"; export const v = callable(() => "one");"""), "One", "a@x");
        await functions.DeployAsync(Code("""import { callable } from "@winche/functions"; export const v = callable(() => "two");"""), "Two", "a@x");
        Assert.Equal("two", (await Call(app, "v", null)).Body["data"]!.GetValue<string>());
        var back = await functions.RollBackAsync(1, null, "a@x");
        Assert.Equal((3, 1, "Back to version 1"), (back.Version, back.RolledBackFrom, back.Note));
        Assert.Equal("one", (await Call(app, "v", null)).Body["data"]!.GetValue<string>());
        Assert.Equal([3, 2, 1], (await app.Service<FunctionStore>().ListAsync()).Select(x => x.Version));
    }
}
