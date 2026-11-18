using Winche.Secrets;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Webhooks;

namespace Winche.Functions;

/// <summary>
/// Functions: deploy (bundle, find the exported functions, check their triggers, keep the version), roll back, and run
/// them. Every server follows the newest deployment within seconds.
/// </summary>
public sealed class WincheFunctions(FunctionStore store, FunctionBundler bundler, DenoRuntime runtime, FunctionsOptions options,
    IServiceProvider services, ILoggerFactory loggers, FunctionSecrets secrets)
{
    public const string LogCategory = "Winche.Functions";
    private static readonly Regex Name = new("^[A-Za-z_$][A-Za-z0-9_$]{0,63}$");
    private readonly ILogger _log = loggers.CreateLogger(LogCategory);
    private volatile Deployment? _current;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly RunSlots _slots = new(() => options.MaxConcurrency);   // the server's limit may change while it runs
    private readonly TaskCompletionSource _warmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private int _inFlight;

    /// <summary>
    /// Done once the deployment in force has its functions' minInstances loaded (or there's none): until then a
    /// server's first callers would wait for code to load. A host's readiness check waits for it.
    /// </summary>
    public Task Warmed => _warmed.Task;

    /// <summary>This server, among those running the project's functions (one per process), and its name as shown.</summary>
    public string ServerId { get; } = Guid.NewGuid().ToString("N");
    public string ServerName { get; } = Environment.MachineName;

    internal static readonly TimeSpan SeenEvery = TimeSpan.FromSeconds(10);

    /// <summary>The other servers running this project's functions now (seen in the last 30 seconds): their names.</summary>
    public async Task<IReadOnlyList<string>> OtherServersAsync(CancellationToken ct = default)
    {
        try { return await store.ServersSeenAsync(SeenEvery * 3, ServerId, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { _log.LogDebug(e, "Couldn't list the other servers."); return []; }
    }

    /// <summary>Runs going on now.</summary>
    public int RunsInFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Stopping (a deploy, a scale-in): new runs are refused as <c>unavailable</c> (apps retry; triggered runs are
    /// delivered again, on another server), and this waits for the runs in flight to end, until <paramref name="ct"/>.
    /// </summary>
    public async Task StopTakingRunsAsync(CancellationToken ct)
    {
        _stopping = true;
        var waited = Stopwatch.StartNew();
        var said = false;
        while (RunsInFlight > 0 && !ct.IsCancellationRequested)
        {
            if (!said) { _log.LogInformation("Stopping: waiting for {Runs} function runs to end.", RunsInFlight); said = true; }
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
        }
        if (RunsInFlight > 0) _log.LogWarning("Stopped with {Runs} function runs still going after {Seconds} s: they end now (triggered ones are delivered again).", RunsInFlight, (int)waited.Elapsed.TotalSeconds);
    }
    private int _watchingSettings;

    /// <summary>
    /// How long <paramref name="fn"/> may run, in seconds: its own time from the settings (Functions → Settings), else
    /// the one its code sets, else the settings' default; never more than <see cref="FunctionsOptions.MaxTimeout"/>.
    /// Before-functions keep their code's time (the write waits for them).
    /// </summary>
    public FunctionLimitsInForce LimitsFor(FunctionInfo fn)
    {
        var settings = services.GetService<Winche.Core.SettingsArea<FunctionsSettings>>()?.Current;
        var own = settings?.Limits.TryGetValue(fn.Name, out var l) == true ? l : null;
        var from = new Dictionary<string, string>();
        T Pick<T>(string key, T? setting, T? code, T fallback) where T : struct
        {
            if (setting is { } s) { from[key] = "setting"; return s; }
            if (code is { } c) { from[key] = "code"; return c; }
            from[key] = "default";
            return fallback;
        }
        var answers = fn.Trigger.Kind is "callable" or "http";   // called by apps: one kept loaded unless it says otherwise
        var memory = Math.Clamp(Pick("memoryMb", own?.MemoryMb, fn.Limits.MemoryMb, settings?.DefaultMemoryMb ?? options.DefaultMemoryMb),
            FunctionLimitRules.MinMemoryMb, options.MaxMemoryMb);
        var max = Math.Clamp(Pick("maxInstances", own?.MaxInstances, fn.Limits.MaxInstances, settings?.DefaultMaxInstances ?? options.DefaultMaxInstances), 1, options.MaxConcurrency);
        var min = Math.Clamp(Pick("minInstances", own?.MinInstances, fn.Limits.MinInstances, answers ? 1 : 0), 0, max);
        var reserved = Math.Clamp(Pick("reservedInstances", own?.ReservedInstances, fn.Limits.ReservedInstances, 0), 0, max);
        var isolate = Pick("isolate", own?.Isolate, fn.Limits.Isolate, false);
        return new FunctionLimitsInForce(memory, min, max, reserved, isolate, from);
    }

    /// <summary>
    /// How long <paramref name="fn"/> may run, in seconds (see above); its instances' limits are <see cref="LimitsFor"/>.
    /// </summary>
    public int TimeoutFor(FunctionInfo fn)
    {
        if (fn.Trigger.Kind == "before") return fn.TimeoutSeconds;
        var settings = services.GetService<Winche.Core.SettingsArea<FunctionsSettings>>()?.Current;
        var seconds = settings?.Timeouts.TryGetValue(fn.Name, out var own) == true ? own
            : fn.CodeTimeoutSeconds ?? settings?.DefaultTimeoutSeconds ?? (int)options.DefaultTimeout.TotalSeconds;
        return Math.Clamp(seconds, 1, (int)options.MaxTimeout.TotalSeconds);
    }

    /// <summary>
    /// The process <paramref name="fn"/> runs in: its own when it's isolated (or asks for more memory than the shared
    /// process's limit) and that fits, else the shared one. Its own process's heap limit is its memory plus room for
    /// garbage between collections.
    /// </summary>
    private string ProcessFor(FunctionInfo fn)
    {
        if (fn.Trigger.Kind == "before") return DenoRuntime.Shared;
        var l = LimitsFor(fn);
        return runtime.Place(fn.Name, l.Isolate || l.MemoryMb > options.SharedProcessMemoryMb, l.MemoryMb + FunctionLimitRules.ProcessRoomMb);
    }

    /// <summary>The deployment in force, or null before the first.</summary>
    public Deployment? Current => _current;

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            var latest = await store.LatestVersionAsync(ct);
            if (latest == 0) _warmed.TrySetResult();   // nothing deployed: nothing to load
            if (latest == (_current?.Version ?? 0)) return;
            var previous = _current;
            _current = latest == 0 ? null : await store.GetAsync(latest, ct);
            if (previous is not null) await runtime.ForgetAsync($"v{previous.Version}", ct);
            if (_current is { } now) _ = WarmAsync(now);
            Changed?.Invoke();
        }
        finally { _refresh.Release(); }
    }

    /// <summary>
    /// Loads the version in force into the runtime and has a worker ready with its code, so the first run after the
    /// server starts or a deploy doesn't wait for it. Best effort: without Deno, or if it fails, runs start it themselves.
    /// </summary>
    private async Task WarmAsync(Deployment d)
    {
        // A change in the console (a function's minInstances, its reservation) applies at once, not only at a deploy.
        if (Interlocked.Exchange(ref _watchingSettings, 1) == 0 && services.GetService<Winche.Core.SettingsArea<FunctionsSettings>>() is { } area)
            area.Changed += changed => { if (_current is { } now) _ = WarmAsync(now); };
        var functions = d.Functions.Where(f => f.Trigger.Kind != "before" && !f.Fresh).ToList();
        _slots.Reserve(functions.ToDictionary(f => f.Name, f => LimitsFor(f).ReservedInstances));
        try
        {
            // Each process keeps its own functions' instances loaded: the shared one all but the isolated ones.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Task.WhenAll(functions.GroupBy(ProcessFor).Select(group =>
                runtime.WarmAsync($"v{d.Version}", d.Bundle, () => ResourceFilesAsync(d, CancellationToken.None),
                    group.ToDictionary(f => f.Name, f => LimitsFor(f).MinInstances), limit.Token, group.Key)));
        }
        catch (Exception e) { _log.LogDebug(e, "Functions version {Version} wasn't warmed up; its first run loads it.", d.Version); }
        finally { _warmed.TrySetResult(); }
    }

    /// <summary>The most resources a deployment may have, and the largest one (bytes).</summary>
    public const int MaxResources = 2000;
    public const long MaxResourceBytes = 64L * 1024 * 1024;

    /// <summary>The content hash resources go by: SHA-256, lowercase hex.</summary>
    public static string ContentHash(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    /// <summary>Which of these resource hashes the server doesn't have yet (the CLI sends only those).</summary>
    public Task<IReadOnlyList<string>> MissingResourcesAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default) => store.MissingResourcesAsync(hashes, ct);

    /// <summary>Stores an resource's content; returns its hash. <paramref name="expectedHash"/>, when given, must match.</summary>
    public async Task<string> PutResourceAsync(byte[] content, string? expectedHash, CancellationToken ct = default)
    {
        if (content.LongLength > MaxResourceBytes) throw new FunctionDeployException($"An resource is at most {MaxResourceBytes / 1024 / 1024} MB.", []);
        var hash = ContentHash(content);
        if (expectedHash is not null && !string.Equals(expectedHash, hash, StringComparison.OrdinalIgnoreCase))
            throw new FunctionDeployException("The resource's content doesn't match its hash (sent incompletely?).", []);
        await store.PutResourceAsync(hash, content, ct);
        return hash;
    }

    /// <summary>What's wrong with an resource list: paths as for files, known hashes, sizes.</summary>
    public static IReadOnlyList<string> ResourceProblems(IReadOnlyDictionary<string, ResourceEntry> resources)
    {
        var problems = new List<string>();
        if (resources.Count > MaxResources) problems.Add($"There are more than {MaxResources} resources.");
        foreach (var (path, a) in resources)
        {
            if (path.Length > 300 || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or ".."))
                problems.Add($"resources/{path} isn't a usable file name (relative, with / between folders).");
            if (a.Hash.Length != 64 || !a.Hash.All(char.IsAsciiHexDigitLower)) problems.Add($"resources/{path}: the hash is SHA-256 in lowercase hex.");
            if (a.Size < 0 || a.Size > MaxResourceBytes) problems.Add($"resources/{path} is larger than {MaxResourceBytes / 1024 / 1024} MB.");
        }
        return problems;
    }

    /// <summary>
    /// Bundles and checks <paramref name="files"/>, then makes them the deployment in force, with <paramref name="resources"/>
    /// (stored beforehand with <see cref="PutResourceAsync"/>) as its resources.
    /// </summary>
    public async Task<Deployment> DeployAsync(IReadOnlyDictionary<string, string> files, string? note, string? by, CancellationToken ct = default, int? rolledBackFrom = null,
        IReadOnlyDictionary<string, ResourceEntry>? resources = null)
    {
        resources ??= new Dictionary<string, ResourceEntry>();
        if (ResourceProblems(resources) is { Count: > 0 } bad) throw new FunctionDeployException("These resources can't be deployed.", bad);
        if (await store.MissingResourcesAsync([.. resources.Values.Select(a => a.Hash)], ct) is { Count: > 0 } missing)
            throw new FunctionDeployException("Some resources haven't been sent.", [.. resources.Where(a => missing.Contains(a.Value.Hash)).Select(a => $"resources/{a.Key}").Take(20)]);
        if (OldApi.Problems(files) is { Count: > 0 } old)
            throw new FunctionDeployException("This code uses the functions API from before @winche-technologies/winche-admin.", old);
        if (bundler.MissingPackages(files) is { Count: > 0 } unnamed)
            throw new FunctionDeployException("The code imports packages its deno.json doesn't name.", unnamed);
        var bundle = await bundler.BundleAsync(files, ct);
        var check = $"check-{Guid.NewGuid():N}";
        JsonArray found;
        try { found = await runtime.AnalyzeAsync(check, bundle, ct); }
        finally { await runtime.ForgetAsync(check, CancellationToken.None); }
        var functions = Read(found, files, out var problems);
        if (problems.Count > 0) throw new FunctionDeployException("These functions can't be deployed.", problems);
        var deployment = await store.AddAsync(files, bundle, functions, note, by, rolledBackFrom, resources, ct);
        _log.LogInformation("Functions version {Version} deployed by {By}: {Functions}.", deployment.Version, by ?? "someone",
            functions.Count == 0 ? "no functions" : string.Join(", ", functions.Select(f => f.Name)));
        await RefreshAsync(ct);
        return deployment;
    }

    /// <summary>Deploys an earlier version's files again, as a new version.</summary>
    public async Task<Deployment> RollBackAsync(int version, string? note, string? by, CancellationToken ct = default)
    {
        var old = await store.GetAsync(version, ct) ?? throw new FunctionDeployException($"There's no version {version}.", []);
        return await DeployAsync(old.Files, note ?? $"Back to version {version}", by, ct, version, old.ResourceFiles);
    }

    private IReadOnlyList<FunctionInfo> Read(JsonArray found, IReadOnlyDictionary<string, string> files, out List<string> problems)
    {
        problems = [];
        var list = new List<FunctionInfo>();
        var catalog = services.GetService<HookCatalog>();
        var maxSeconds = (int)options.MaxTimeout.TotalSeconds;
        foreach (var node in found.OfType<JsonObject>())
        {
            var name = node["name"]!.GetValue<string>();
            if ((int?)node["protocol"] is { } protocol && protocol != FunctionsSdk.Protocol)
            {
                problems.Add($"{name}: written with an @winche-technologies/winche-functions for protocol {protocol}; this server speaks {FunctionsSdk.Protocol} (@winche-technologies/winche-functions {FunctionsSdk.Major}.x).");
                continue;
            }
            var t = node["trigger"] as JsonObject ?? [];
            var trigger = new FunctionTrigger(t["kind"]?.GetValue<string>() ?? "", (t["events"] as JsonArray)?.Select(e => e!.GetValue<string>()).ToList(),
                t["path"]?.GetValue<string>(), t["cron"]?.GetValue<string>(), t["timeZone"]?.GetValue<string>(), t["on"]?.GetValue<string>());
            var isBefore = trigger.Kind == "before";
            var timeout = node["options"]?["timeoutSeconds"]?.GetValue<double>() is { } s ? (int)Math.Ceiling(s)
                : isBefore ? (int)BeforeFunctions.DefaultTimeout.TotalSeconds : (int)options.DefaultTimeout.TotalSeconds;
            var limit = isBefore ? (int)BeforeFunctions.MaxTimeout.TotalSeconds : maxSeconds;
            if (!Name.IsMatch(name)) problems.Add($"{name}: a function's name is its export name, letters and digits.");
            if (timeout < 1 || timeout > limit) problems.Add($"{name}: timeoutSeconds is 1 to {limit}{(isBefore ? " for a before-function (the write waits for it)" : "")}.");
            if (isBefore)
            {
                var expected = trigger.On switch { "documents" or "files" => new[] { "write" }, "users" => ["create", "signIn"], _ => [] };
                if (expected.Length == 0) problems.Add($"{name}: a before-function is on documents, files or users.");
                else if (trigger.Events is not { Count: > 0 } events || events.Any(e => !expected.Contains(e))) problems.Add($"{name}: unknown before-event.");
                if (trigger.On is "documents" or "files")
                {
                    if (string.IsNullOrWhiteSpace(trigger.Path)) problems.Add($"{name}: give a path, e.g. \"patients/{{patientId}}\".");
                    else if (!PathPattern.TryParse(trigger.Path, out _, out var error)) problems.Add($"{name}: {error}");
                }
                var binding = trigger.On switch { "documents" => "db", "files" => "files", "users" => "users", _ => null };
                if (binding is not null && !services.GetServices<IFunctionBinding>().Any(b => b.Name == binding))
                    problems.Add($"{name}: this app doesn't run before-functions on {trigger.On} (add Winche.Functions.{trigger.On switch { "documents" => "Database", "files" => "Storage", _ => "Auth" }}).");
            }
            var source = trigger.Kind switch { "documents" => "database", "files" => "storage", "users" => "auth", "schedule" => "schedule", "assets" => "assets", _ => null };
            if (trigger.Kind is "documents" or "files")
            {
                if (string.IsNullOrWhiteSpace(trigger.Path)) problems.Add($"{name}: give a path, e.g. \"patients/{{patientId}}\".");
                else if (!PathPattern.TryParse(trigger.Path, out _, out var error)) problems.Add($"{name}: {error}");
            }
            if (trigger.Kind == "schedule")
            {
                try { Cronos.CronExpression.Parse(trigger.Cron ?? ""); }
                catch (Cronos.CronFormatException ex) { problems.Add($"{name}: '{trigger.Cron}' isn't a cron schedule (minute hour day month weekday): {ex.Message}"); }
                if (trigger.TimeZone is { } tz && !TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)) problems.Add($"{name}: '{tz}' isn't a time zone (e.g. Europe/London).");
            }
            if (source is not null && catalog?.Sources.Any(x => x.Name == source) != true)
                problems.Add(source == "schedule" || catalog is null
                    ? $"{name}: triggers need Winche.Webhooks in this app (AddWincheHooks)."
                    : source == "assets" ? $"{name}: this app has no assets (add Winche.Assets: AddWincheAssets)."
                    : $"{name}: this app has no {source} events (add Winche.Webhooks.{char.ToUpperInvariant(source[0])}{source[1..]}).");
            if (trigger.Kind == "http" && trigger.Events is { } methods && methods.Any(m => m is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD")))
                problems.Add($"{name}: methods are GET, POST, PUT, PATCH, DELETE or HEAD.");
            if (trigger.Kind == "assets" && (trigger.Events ?? []).Any(e => e is not ("released" or "channelMoved")))
                problems.Add($"{name}: assets events are released and channelMoved.");
            if (trigger.Kind is not ("documents" or "files" or "users" or "schedule" or "callable" or "before" or "consolePage" or "http" or "assets"))
                problems.Add($"{name}: '{trigger.Kind}' isn't a trigger.");
            string? minRole = null;
            ConsolePageInfo? page = null;
            if (trigger.Kind is "consolePage")
            {
                minRole = node["options"]?["minRole"]?.GetValueKind() == JsonValueKind.String ? node["options"]!["minRole"]!.GetValue<string>() : "viewer";
                if (ConsolePages.Rank(minRole) < 0) problems.Add($"{name}: minRole is viewer, member or admin.");
            }
            if (trigger.Kind == "consolePage")
            {
                var o = node["options"] as JsonObject ?? [];
                var title = o["title"]?.GetValueKind() == JsonValueKind.String ? o["title"]!.GetValue<string>().Trim() : "";
                var folder = o["files"]?.GetValueKind() == JsonValueKind.String ? ConsolePages.Folder(o["files"]!.GetValue<string>()) : null;
                var icon = o["icon"]?.GetValueKind() == JsonValueKind.String ? o["icon"]!.GetValue<string>() : null;
                var order = o["order"]?.GetValueKind() == JsonValueKind.Number ? (int)o["order"]!.GetValue<double>() : 0;
                if (title.Length is 0 or > 60) problems.Add($"{name}: give the page a title (up to 60 characters).");
                if (folder is null) problems.Add($"{name}: files is the folder of the page's files, next to index.ts (e.g. \"console/reports\").");
                else if (!files.ContainsKey($"{folder}/index.html")) problems.Add($"{name}: there's no {folder}/index.html in what was deployed.");
                if (icon is not null && !ConsolePages.IconName.IsMatch(icon)) problems.Add($"{name}: icon is a Tabler icon name, e.g. \"chart-bar\".");
                page = new ConsolePageInfo(title, folder ?? "", icon, order);
            }
            List<string>? declared = null;
            if (node["options"]?["secrets"] is { } given)
            {
                if (given is not JsonArray items || items.Any(i => i?.GetValueKind() != JsonValueKind.String))
                    problems.Add($"{name}: secrets is a list of names, e.g. [\"SES_SECRET_KEY\"].");
                else
                {
                    declared = [.. items.Select(i => i!.GetValue<string>()).Distinct()];
                    foreach (var secret in declared) if (FunctionSecrets.Problem(secret) is { } bad) problems.Add($"{name}: {bad}");
                    if (declared.Count > 0 && !secrets.Available) problems.Add($"{name}: {FunctionSecrets.Unavailable}");
                    if (declared.Count == 0) declared = null;
                }
            }
            var codeTimeout = node["options"]?["timeoutSeconds"]?.GetValue<double>() is { } cs ? (int)Math.Ceiling(cs) : (int?)null;
            // What it's for and the shapes it takes and returns: shown in the console, never checked.
            string? about = null;
            if (node["options"]?["description"] is { } d)
            {
                if (d.GetValueKind() != JsonValueKind.String || d.GetValue<string>().Trim() is not { Length: > 0 and <= 300 } text)
                    problems.Add($"{name}: description is a sentence of up to 300 characters.");
                else about = text;
            }
            static JsonNode? Shape(JsonNode node, string name, string key, bool schema, List<string> problems)
            {
                if (node["options"]?[key] is not { } value) return null;
                if (schema && value is not JsonObject) { problems.Add($"{name}: {key} is a JSON Schema object, e.g. {{ type: \"object\", properties: {{ … }} }}."); return null; }
                if (value.ToJsonString().Length > 16_384) { problems.Add($"{name}: {key} is at most 16 KB."); return null; }
                return value.DeepClone();
            }
            var opts = node["options"];
            var notWhole = new List<string>();
            int? Whole(string key)
            {
                if (opts?[key] is not JsonValue v) return null;
                if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue) return (int)d;
                notWhole.Add($"{name}: {key} is a whole number.");
                return null;
            }
            var limits = new FunctionLimits(Whole("memory"), Whole("minInstances"), Whole("maxInstances"), Whole("reservedInstances"),
                opts?["isolate"] is JsonValue iv && iv.TryGetValue<bool>(out var isolate) ? isolate : null);
            problems.AddRange(notWhole);
            if (isBefore && limits != new FunctionLimits())
                problems.Add($"{name}: a before-function runs inside the write: memory, instances and isolate are for other functions.");
            else problems.AddRange(FunctionLimitRules.Problems(name, limits, options.MaxMemoryMb, options.MaxConcurrency));
            list.Add(new FunctionInfo(name, trigger, timeout, isBefore && node["options"]?["failOpen"]?.GetValue<bool>() == true, declared, minRole, page, codeTimeout,
                about, Shape(node, name, "input", true, problems), Shape(node, name, "output", true, problems), Shape(node, name, "example", false, problems),
                node["options"]?["fresh"]?.GetValue<bool>() == true) { Limits = limits });
        }
        var reservedInAll = list.Sum(f => f.Limits.ReservedInstances ?? 0);
        if (reservedInAll > options.MaxConcurrency)
            problems.Add($"Functions reserve {reservedInAll} runs at once in all; this server allows {options.MaxConcurrency}.");
        return list;
    }

    /// <summary>
    /// Runs function <paramref name="name"/> of the deployment in force with <paramref name="arg"/>. Its ctx.* calls act with the
    /// server's rights; its output goes to the logs (category Winche.Functions). Never throws for the function's own failures.
    /// </summary>
    public async Task<FunctionResult> InvokeAsync(string name, JsonNode? arg, FunctionCaller caller, CancellationToken ct = default, string? kind = null) =>
        await InvokeAsync(name, arg, caller, ct, kind, readOnly: false);

    /// <summary>As above; with <paramref name="readOnly"/> its ctx.* calls may only read (before-functions).</summary>
    internal Task<FunctionResult> InvokeAsync(string name, JsonNode? arg, FunctionCaller caller, CancellationToken ct, string? kind, bool readOnly) =>
        InvokeAsync(name, arg, caller, ct, kind, readOnly, 0);

    /// <summary>
    /// A deployment's resources as files on this machine (path → file), written from the database the first time they're
    /// needed. Each content is one file named by its hash, so versions share them.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResourceFilesAsync(Deployment d, CancellationToken ct)
    {
        var dir = DenoRuntime.WorkFolder("resources");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, a) in d.ResourceFiles)
        {
            var file = Path.Combine(dir, a.Hash);
            if (!File.Exists(file))
            {
                var content = await store.ResourceAsync(a.Hash, ct) ?? throw new FunctionsUnavailableException($"The resource resources/{path} is missing from the database.");
                var temp = file + "." + Guid.NewGuid().ToString("N") + ".part";
                await File.WriteAllBytesAsync(temp, content, ct);
                File.Move(temp, file, overwrite: true);
            }
            map[path] = file;
        }
        return map;
    }

    private async Task<FunctionResult> InvokeAsync(string name, JsonNode? arg, FunctionCaller caller, CancellationToken ct, string? kind, bool readOnly, int _)
    {
        if (_stopping) return FunctionResult.Failed("unavailable", "This server is stopping: try again.");
        Interlocked.Increment(ref _inFlight);
        try { return await RunAsync(name, arg, caller, ct, kind, readOnly); }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    private async Task<FunctionResult> RunAsync(string name, JsonNode? arg, FunctionCaller caller, CancellationToken ct, string? kind, bool readOnly)
    {
        if (_current is null) await RefreshAsync(ct);
        var d = _current;
        var fn = d?.Functions.FirstOrDefault(f => f.Name == name && (kind is null || f.Trigger.Kind == kind));
        if (d is null || fn is null) return FunctionResult.Failed("not-found", $"There's no function {name}.");
        var logs = new List<FunctionLogLine>();
        Dictionary<string, string?> secretValues;
        try { secretValues = fn.Secrets is { Count: > 0 } names ? await secrets.ForRunAsync(names, ct) : []; }
        catch (SecretReferenceException unreadable) { return FunctionResult.Failed("failed-precondition", unreadable.Message); }   // e.g. AWS refused: the reason, never a value
        var hidden = secretValues.Values.OfType<string>().ToList();
        string Hide(string text) => hidden.Count == 0 ? text : FunctionSecrets.Hide(text, hidden);
        async Task<JsonNode?> Bridge(string op, JsonObject args)
        {
            if (readOnly && !AdminOps.IsRead(op))
                throw new FunctionCallException("failed-precondition", $"@winche-technologies/winche-admin can't change anything in a before-function ({op}): it would run outside the write being checked. Use a trigger (onDocument.created, …).");
            return await AdminOps.RunAsync(services, op, args, ct);
        }
        void Log(string level, string message)
        {
            message = Hide(message);
            lock (logs) logs.Add(new FunctionLogLine(level, message, DateTimeOffset.UtcNow));
            var lvl = level switch { "error" => LogLevel.Error, "warn" => LogLevel.Warning, "debug" => LogLevel.Debug, _ => LogLevel.Information };
            using (_log.BeginScope(new Dictionary<string, object?> { ["Function"] = name }))
                _log.Log(lvl, "{Function}: {Message}", name, message);
        }
        var context = new JsonObject
        {
            ["functionName"] = name,
            ["auth"] = caller.Uid is null ? null : new JsonObject { ["uid"] = caller.Uid, ["token"] = JsonSerializer.SerializeToNode(caller.Token ?? new Dictionary<string, object?>()) },
            ["secrets"] = new JsonObject(secretValues.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)(kv.Value is null ? null : JsonValue.Create(kv.Value))))),
        };
        var watch = Stopwatch.StartNew();
        var limits = LimitsFor(fn);
        var timeout = TimeSpan.FromSeconds(TimeoutFor(fn));
        // Its turn: at most its maxInstances at once, a reserved slot of its own or a shared one; waiting counts
        // against its time.
        using var slot = await _slots.TakeAsync(name, limits.MaxInstances, timeout, ct);
        if (slot is null)
        {
            _log.LogWarning("{Function} waited {Seconds} s for a free instance ({Max} at once) and didn't run.", name, (int)timeout.TotalSeconds, limits.MaxInstances);
            return FunctionResult.Failed("resource-exhausted", $"{name} is busy: {limits.MaxInstances} runs at once, and none ended within its time.");
        }
        var left = timeout - watch.Elapsed;
        JsonObject done;
        try { done = await runtime.InvokeAsync($"v{d.Version}", d.Bundle, name, arg, context, left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), Bridge, Log, ct,
                () => ResourceFilesAsync(d, ct), fresh: fn.Fresh, memoryMb: fn.Trigger.Kind == "before" ? null : limits.MemoryMb,
                process: ProcessFor(fn)); }
        catch (FunctionsUnavailableException ex) { return FunctionResult.Failed("unavailable", ex.Message); }
        var ms = (int)watch.ElapsedMilliseconds;
        if (done["retired"] is JsonObject retired)
        {
            var reason = retired["reason"]?.GetValue<string>() ?? "crash";
            var heap = (int?)retired["heapMb"];
            var message = reason switch
            {
                "memory" => $"Instance replaced: holding {heap} MB after a run, over its {(int?)retired["limitMb"]} MB. Keep less at the top of its module, or give it more memory.",
                "timeout" => "Instance replaced: a run went past its time.",
                _ => "Instance replaced: it crashed.",
            };
            if (reason == "memory") _log.LogWarning("{Function}: {Message}", name, message);
            runtime.Note(new FunctionEvent(DateTimeOffset.UtcNow, name, ProcessFor(fn) is { Length: > 0 } own ? own : null, reason, message));
        }
        if (done["error"] is JsonObject e)
        {
            var code = e["code"]?.GetValue<string>() ?? "internal";
            var message = Hide(e["message"]?.GetValue<string>() ?? "The function failed.");
            using (_log.BeginScope(new Dictionary<string, object?> { ["Function"] = name }))
                _log.LogWarning("{Function} failed after {Ms} ms ({Code}): {Message}{Stack}", name, ms, code, message,
                    e["stack"]?.GetValue<string>() is { } stack ? "\n" + Hide(stack) : "");
            return new FunctionResult(false, null, code, message, e["https"]?.GetValue<bool>() == true, ms, logs) { ErrorDetails = e["details"]?.DeepClone() };
        }
        using (_log.BeginScope(new Dictionary<string, object?> { ["Function"] = name }))
            _log.LogInformation("{Function} ran in {Ms} ms.", name, ms);
        return new FunctionResult(true, done["result"]?.DeepClone(), null, null, false, ms, logs);
    }
}
