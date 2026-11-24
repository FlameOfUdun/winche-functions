using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Winche.Webhooks;

namespace Winche.Functions;

/// <summary>
/// Each deployed function with a document, file, user or schedule trigger is a hook (id <c>fn.&lt;name&gt;</c>): events reach
/// it through Winche.Webhooks' queue, so they're delivered at least once, retried, and listed under the function's Runs (console → Functions).
/// </summary>
internal sealed class FunctionHooks(WincheFunctions functions) : IHookProvider
{
    /// <summary>
    /// Only functions that events start: callables, HTTP functions, console pages and actions are called, never sent
    /// events (as hooks without events of their own, they'd run on every schedule's tick).
    /// </summary>
    internal static bool IsTriggered(FunctionInfo f) => f.Trigger.Kind is "documents" or "files" or "users" or "schedule" or "assets" or "realtime";

    public IReadOnlyList<HookSettings> Hooks => [.. (functions.Current?.Functions ?? []).Where(IsTriggered).Select(f => new HookSettings
    {
        Id = $"fn.{f.Name}", Name = $"Function {f.Name}", Enabled = true,
        When = new HookTrigger
        {
            Source = Source(f.Trigger.Kind),
            Events = f.Trigger.Kind == "schedule" ? [f.Name] : f.Trigger.Events ?? [],
            Path = f.Trigger.Path,
        },
        Do = new HookActionSettings { Kind = RunFunctionAction.ActionName, Options = new Dictionary<string, string> { ["function"] = f.Name } },
    })];

    internal static string Source(string kind) => kind switch { "documents" => "database", "files" => "storage", "users" => "auth", "assets" => "assets", "realtime" => "realtime", _ => "schedule" };
}

/// <summary>The hook action that runs a function with the event (for functions' triggers; the console doesn't offer it for webhooks).</summary>
internal sealed class RunFunctionAction(IServiceProvider services) : IWincheAction
{
    public const string ActionName = "functions.run";
    public string Name => ActionName;
    public string Title => "Run a function";
    public string Description => "Runs a deployed function with the event, as its trigger would.";
    public IReadOnlyList<string> Sources => [];
    public IReadOnlyList<WincheActionOption> Options { get; } = [new("function", "Function", "onVisit", Help: "Its export name.")];

    public async Task<string> RunAsync(WincheActionContext context, CancellationToken ct)
    {
        var functions = services.GetRequiredService<WincheFunctions>();
        var name = context.Options["function"];
        var fn = functions.Current?.Functions.FirstOrDefault(f => f.Name == name)
            ?? throw new InvalidOperationException($"There's no function {name} deployed.");
        var result = await functions.InvokeAsync(name, Argument(fn, context.Change), FunctionCaller.Server, ct);
        if (!result.Ok) throw new InvalidOperationException($"{name} failed ({result.ErrorCode}): {result.ErrorMessage}");
        return $"{name} ran in {result.DurationMs} ms.";
    }

    /// <summary>What the function's handler gets for this event.</summary>
    internal static JsonObject Argument(FunctionInfo fn, WincheChange e)
    {
        var captures = new JsonObject();
        if (fn.Trigger.Path is { } p && e.Path is { } path && PathPattern.TryParse(p, out var pattern, out _) && pattern!.Match(path) is { } m)
            foreach (var (k, v) in m) captures[k] = v;
        return e.Source switch
        {
            "database" => new JsonObject
            {
                ["type"] = e.Type, ["path"] = e.Path, ["params"] = captures, ["data"] = e.Data?.DeepClone(), ["before"] = e.Before?.DeepClone(),
                ["id"] = e.Id, ["at"] = e.At.ToString("O"),
            },
            "storage" => new JsonObject
            {
                ["type"] = e.Type, ["path"] = e.Path, ["params"] = captures, ["file"] = e.Data?.DeepClone(), ["id"] = e.Id, ["at"] = e.At.ToString("O"),
            },
            "assets" => new JsonObject
            {
                ["type"] = e.Type, ["release"] = e.Data?["release"]?.DeepClone(), ["channel"] = e.Data?["channel"]?.DeepClone(),
                ["note"] = e.Data?["note"]?.DeepClone(), ["by"] = e.Data?["by"]?.DeepClone(), ["id"] = e.Id, ["at"] = e.At.ToString("O"),
            },
            "realtime" => new JsonObject
            {
                ["type"] = e.Type, ["path"] = e.Path, ["params"] = captures, ["list"] = e.Data?.DeepClone(), ["id"] = e.Id, ["at"] = e.At.ToString("O"),
            },
            "auth" => new JsonObject
            {
                ["type"] = e.Type, ["uid"] = e.Variables.GetValueOrDefault("uid"), ["user"] = e.Data?.DeepClone(),
                ["provider"] = e.Variables.GetValueOrDefault("provider") is { Length: > 0 } pr ? pr : null, ["id"] = e.Id, ["at"] = e.At.ToString("O"),
            },
            _ => new JsonObject { ["scheduledAt"] = e.Data?["scheduledAt"]?.DeepClone() ?? e.At.ToString("O"), ["id"] = e.Id },
        };
    }
}

/// <summary>
/// Starts scheduled functions when they're due, on one server at a time (a Postgres advisory lock). A run that was
/// missed while every server was down happens once when one is back, not once per missed time.
/// </summary>
internal sealed class FunctionScheduler(WincheFunctions functions, FunctionStore store, IServiceProvider services, TimeProvider time,
    ILogger<FunctionScheduler> logger) : BackgroundService
{
    internal static TimeSpan Tick = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Starting scheduled functions failed; trying again shortly."); }
            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task RunDueAsync(CancellationToken ct)
    {
        var schedules = functions.Current?.Functions.Where(f => f.Trigger.Kind == "schedule").ToList() ?? [];
        if (schedules.Count == 0 || services.GetService<HookEvents>() is not { } events) return;
        await store.InitializeAsync(ct);
        await using var conn = await store.Db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", conn, tx))
        {
            // One scheduler per project: several projects' functions can share a database, each in its own schema.
            var schema = new NpgsqlConnectionStringBuilder(store.Db.ConnectionString).SearchPath;
            lockCmd.Parameters.AddWithValue("key", Winche.Core.AdvisoryLease.KeyFor($"functions-scheduler:{conn.Database}:{schema}"));
            if (!(bool)(await lockCmd.ExecuteScalarAsync(ct))!) return;   // another server is on it
        }
        var now = time.GetUtcNow();
        foreach (var fn in schedules)
        {
            var cron = Cronos.CronExpression.Parse(fn.Trigger.Cron!);
            var zone = fn.Trigger.TimeZone is { } tz ? TimeZoneInfo.FindSystemTimeZoneById(tz) : TimeZoneInfo.Utc;
            DateTimeOffset? last = null;
            await using (var get = new NpgsqlCommand("SELECT last_due FROM winche_function_schedules WHERE name = $1", conn, tx))
            {
                get.Parameters.AddWithValue(fn.Name);
                if (await get.ExecuteScalarAsync(ct) is DateTime dt) last = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            }
            if (last is null)
            {
                await SaveAsync(conn, tx, fn.Name, now, ct);   // starts counting from now: no run for times before it existed
                continue;
            }
            DateTimeOffset? due = null;
            for (var next = cron.GetNextOccurrence(last.Value, zone); next is { } n && n <= now; next = cron.GetNextOccurrence(n, zone)) due = n;
            if (due is null) continue;
            await events.RaiseAsync(new WincheChange("schedule", fn.Name, null, now, new JsonObject { ["scheduledAt"] = due.Value.ToString("O") }, null,
                new Dictionary<string, string>()), ct);
            await SaveAsync(conn, tx, fn.Name, due.Value, ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task SaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string name, DateTimeOffset due, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO winche_function_schedules (name, last_due) VALUES ($1, $2) ON CONFLICT (name) DO UPDATE SET last_due = EXCLUDED.last_due
            """, conn, tx);
        cmd.Parameters.AddWithValue(name);
        cmd.Parameters.AddWithValue(due);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Follows deployments made on other servers.</summary>
internal sealed class FunctionsSync(WincheFunctions functions, FunctionStore store, FunctionsOptions options, ILogger<FunctionsSync> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken ct)
    {
        await store.InitializeAsync(ct);
        await functions.RefreshAsync(ct);
        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seen = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            // This server runs the project's functions: noted every few seconds, so the console can say how many do.
            if (DateTimeOffset.UtcNow - seen >= WincheFunctions.SeenEvery)
            {
                try { await store.SeenAsync(functions.ServerId, functions.ServerName, stoppingToken); seen = DateTimeOffset.UtcNow; }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Couldn't note this server as running functions."); }
            }
            try { await Task.Delay(options.SyncInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
            try { await functions.RefreshAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Couldn't check for a new functions deployment."); }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        try { await store.GoneAsync(functions.ServerId, ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Couldn't note this server as stopped."); }
    }
}
