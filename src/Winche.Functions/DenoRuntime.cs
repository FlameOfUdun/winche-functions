using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Winche.Functions;

/// <summary>A problem with the functions runtime itself (no Deno, it failed to start), in a sentence.</summary>
public sealed class FunctionsUnavailableException(string message) : Exception(message);

/// <summary>
/// The Deno processes that run a project's functions: the shared one (every function that isn't isolated) and one per
/// isolated function, each started on first use with a heap limit per worker, and started again if it dies. A process
/// that runs out of memory fails only its own runs. Each run is a sandboxed worker (network only); requests and answers
/// are JSON lines on a process's stdin and stdout.
/// </summary>
public sealed class DenoRuntime : IAsyncDisposable
{
    /// <summary>The shared process's name.</summary>
    public const string Shared = "";

    private readonly FunctionsOptions _options;
    private readonly ILogger<DenoRuntime> _logger;
    private readonly FunctionsMemoryBudget _budget;
    private readonly ConcurrentDictionary<string, DenoProcess> _processes = new();
    private readonly Dictionary<string, string> _bundleFiles = [];   // version -> its bundle's file
    private readonly HashSet<string> _refused = [];   // isolated functions told they run in the shared process
    private readonly Lock _placing = new();
    private readonly Timer _idle;
    private long _next;

    public DenoRuntime(FunctionsOptions options, ILogger<DenoRuntime> logger)
    {
        _options = options;
        _logger = logger;
        _budget = options.MemoryBudget ??= FunctionsMemoryBudget.FromContainer();
        _idle = new Timer(_ => StopIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>The deno program to use, or why there isn't one.</summary>
    public static string Locate(FunctionsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DenoPath))
            return File.Exists(options.DenoPath) ? options.DenoPath : throw new FunctionsUnavailableException($"Deno isn't at {options.DenoPath} (FunctionsOptions.DenoPath).");
        var name = OperatingSystem.IsWindows() ? "deno.exe" : "deno";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FunctionsUnavailableException("Functions need Deno (https://deno.com): install it, or set FunctionsOptions.DenoPath.");
    }

    internal static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        return new StreamReader(stream).ReadToEnd();
    }

    internal static string WorkFolder(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "winche-functions", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The processes running now: the shared one ("") and isolated functions', with their heap limits.</summary>
    public IReadOnlyDictionary<string, int> Processes =>
        _processes.Where(p => p.Value.Running).ToDictionary(p => p.Key, p => p.Value.HeapMb);

    /// <summary>The functions' memory budget this runtime counts against.</summary>
    public FunctionsMemoryBudget Budget => _budget;

    private readonly LinkedList<FunctionEvent> _events = new();

    /// <summary>What happened to instances and processes lately (newest first, at most 200): replacements, out of memory, restarts.</summary>
    public IReadOnlyList<FunctionEvent> Events { get { lock (_events) return [.. _events]; } }

    /// <summary>Records an event (and keeps the last 200).</summary>
    public void Note(FunctionEvent e)
    {
        lock (_events)
        {
            _events.AddFirst(e);
            while (_events.Count > 200) _events.RemoveLast();
        }
    }

    /// <summary>Each process of the project (running or not) and its instances now.</summary>
    public async Task<IReadOnlyList<FunctionProcessInfo>> StatsAsync(CancellationToken ct)
    {
        var list = new List<FunctionProcessInfo>();
        foreach (var (name, p) in _processes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            IReadOnlyList<FunctionInstanceInfo> instances = [];
            if (p.Running)
            {
                try
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(TimeSpan.FromSeconds(3));
                    var done = await p.AskAsync(new JsonObject { ["t"] = "stats" }, wait.Token);
                    instances = (done["result"] as JsonArray ?? []).OfType<JsonObject>().Select(i => new FunctionInstanceInfo(
                        i["function"]?.GetValue<string>() ?? "", i["state"]?.GetValue<string>() ?? "idle", (int?)i["heapMb"], (int?)i["runs"] ?? 0,
                        (int?)i["ageSeconds"] ?? 0, (int?)i["busySeconds"])).ToList();
                }
                catch (Exception e) when (e is OperationCanceledException or FunctionsUnavailableException) { }
            }
            list.Add(new FunctionProcessInfo(name == Shared ? null : name, p.Running, p.Pid, p.HeapMb, p.StartedAt, p.Starts, instances));
        }
        return list;
    }

    /// <summary>
    /// Where a function runs: its own process when it asks to be isolated (<paramref name="heapMb"/>: that process's
    /// limit) and the budget and the project's process count allow, else the shared one. Said once in the logs when it
    /// can't have its own.
    /// </summary>
    public string Place(string function, bool isolate, int heapMb)
    {
        if (!isolate)
        {
            // No longer isolated: its own process goes (its memory back in the budget).
            if (_processes.TryRemove(function, out var gone)) { _budget.Release(gone.HeapMb); _ = gone.DisposeAsync().AsTask(); }
            return Shared;
        }
        lock (_placing)
        {
            if (_processes.TryGetValue(function, out var existing))
            {
                if (existing.HeapMb == heapMb) return function;
                _processes.TryRemove(function, out _);   // its limit changed: started again with the new one
                _budget.Release(existing.HeapMb);
                _ = existing.DisposeAsync().AsTask();
            }
            var isolated = _processes.Keys.Count(k => k != Shared);
            string? why = isolated + 1 >= _options.MaxProcesses ? $"this project already has {_options.MaxProcesses} processes (FunctionsOptions.MaxProcesses)"
                : !_budget.TryReserve(heapMb) ? $"its {heapMb} MB doesn't fit the functions' memory budget ({_budget.Describe()})"
                : null;
            if (why is null)
            {
                _processes[function] = new DenoProcess(this, function, heapMb, reserved: true);
                _refused.Remove(function);
                return function;
            }
            if (_refused.Add(function))
                _logger.LogWarning("{Function} asks for a process of its own, but runs in the shared one: {Why}.", function, why);
            return Shared;
        }
    }

    /// <summary>Why <paramref name="function"/> runs in the shared process though it asks for its own, or null.</summary>
    public bool Refused(string function) { lock (_placing) return _refused.Contains(function); }

    private DenoProcess Process(string name) =>
        _processes.GetOrAdd(name, n => n == Shared ? new DenoProcess(this, Shared, _budget.Take(_options.SharedProcessMemoryMb), reserved: false)
            : throw new InvalidOperationException($"No process for {n}: Place it first."));

    /// <summary>Stops isolated processes nothing has used for a while (their memory back in the budget), unless they keep instances loaded.</summary>
    private void StopIdle()
    {
        lock (_placing)
        foreach (var (name, p) in _processes)
            if (name != Shared && p.Idle(TimeSpan.FromMinutes(5)) && _processes.TryRemove(name, out _))
            {
                _budget.Release(p.HeapMb);
                _ = p.DisposeAsync().AsTask();
                _logger.LogInformation("{Function}'s process stopped: idle.", name);
            }
    }

    /// <summary>The bundle file for a version's code (named by its content, so Deno compiles it once and reuses its code cache).</summary>
    private async Task<string> BundleFileAsync(string version, string code, CancellationToken ct)
    {
        var file = Path.Combine(WorkFolder("bundles"), WincheFunctions.ContentHash(Encoding.UTF8.GetBytes(code)) + ".js");
        if (!File.Exists(file))
        {
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".part";
            await File.WriteAllTextAsync(temp, code, new UTF8Encoding(false), ct);
            File.Move(temp, file, overwrite: true);
        }
        lock (_bundleFiles) _bundleFiles[version] = file;
        return file;
    }

    /// <summary>Drops a bundle from every process (an analyzed one, or an old version), and its file unless another version uses it.</summary>
    public async Task ForgetAsync(string version, CancellationToken ct)
    {
        string? unused = null;
        lock (_bundleFiles)
            if (_bundleFiles.Remove(version, out var file) && !_bundleFiles.ContainsValue(file)) unused = file;
        foreach (var p in _processes.Values) await p.ForgetAsync(version, ct);
        if (unused is not null) { try { File.Delete(unused); } catch (IOException) { } }
    }

    /// <summary>The exported functions of a bundle and their triggers (run in a worker without network, in the shared process).</summary>
    public async Task<JsonArray> AnalyzeAsync(string version, string code, CancellationToken ct)
    {
        var done = await Process(Shared).RequestAsync(new JsonObject { ["t"] = "analyze", ["version"] = version }, version, code,
            new Pending(new(TaskCreationOptions.RunContinuationsAsynchronously), null, null), ct);
        if (done["error"] is JsonObject e) throw new FunctionDeployException("The code doesn't load.", [e["message"]?.GetValue<string>() ?? "It failed to load."]);
        return done["result"] as JsonArray ?? [];
    }

    /// <summary>Runs function <paramref name="name"/> in process <paramref name="process"/> (from <see cref="Place"/>); <paramref name="bridge"/> answers its ctx calls, <paramref name="log"/> gets its output.</summary>
    public async Task<JsonObject> InvokeAsync(string version, string code, string name, JsonNode? arg, JsonObject context, TimeSpan timeout,
        Func<string, JsonObject, Task<JsonNode?>> bridge, Action<string, string> log, CancellationToken ct,
        Func<Task<IReadOnlyDictionary<string, string>>>? resources = null, bool fresh = false, int? memoryMb = null, string process = Shared)
    {
        // How many run at once is the slots' job (WincheFunctions): per function, reserved and in all.
        {
            return await Process(process).RequestAsync(new JsonObject
            {
                ["t"] = "invoke", ["version"] = version, ["name"] = name, ["arg"] = arg?.DeepClone(), ["context"] = context,
                ["timeoutMs"] = (long)timeout.TotalMilliseconds, ["fresh"] = fresh, ["memoryMb"] = memoryMb,
            }, version, code, new Pending(new(TaskCreationOptions.RunContinuationsAsynchronously), bridge, log), ct, resources);
        }
    }

    /// <summary>
    /// Starts process <paramref name="process"/> if it isn't, gives it the version's code, and keeps
    /// <paramref name="instances"/> of each named function loaded there (its minInstances; 0 lets idle ones go).
    /// </summary>
    public Task WarmAsync(string version, string code, Func<Task<IReadOnlyDictionary<string, string>>>? resources,
        IReadOnlyDictionary<string, int> instances, CancellationToken ct, string process = Shared) =>
        Process(process).WarmAsync(version, code, resources, instances, ct);

    public async ValueTask DisposeAsync()
    {
        await _idle.DisposeAsync();
        foreach (var (_, p) in _processes)
        {
            _budget.Release(p.HeapMb);
            await p.DisposeAsync();
        }
        _processes.Clear();
    }

    private sealed record Pending(TaskCompletionSource<JsonObject> Done, Func<string, JsonObject, Task<JsonNode?>>? Bridge, Action<string, string>? Log);

    private static JsonObject Error(string code, string message) =>
        new() { ["t"] = "done", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /// <summary>One Deno process: started on first use with its heap limit per worker, started again if it dies.</summary>
    private sealed class DenoProcess(DenoRuntime runtime, string name, int heapMb, bool reserved) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _start = new(1, 1);
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly ConcurrentDictionary<long, Pending> _pending = new();
        private readonly HashSet<string> _sent = [];
        private sealed record Warmth(string Version, string Code, Func<Task<IReadOnlyDictionary<string, string>>>? Resources, IReadOnlyDictionary<string, int> Instances);
        private readonly Dictionary<string, Warmth> _kept = [];   // version -> instances kept loaded here
        private int _starts;
        private Process? _process;
        private TaskCompletionSource? _ready;
        private volatile bool _outOfMemory;
        private long _lastUsed = Environment.TickCount64;

        public int HeapMb => heapMb;
        public int? Pid => _process is { HasExited: false } p ? p.Id : null;
        public DateTimeOffset? StartedAt { get; private set; }
        public int Starts => _starts;
        public bool Reserved => reserved;
        public bool Running => _process is { HasExited: false };
        private string Label => name == Shared ? "the shared functions process" : $"{name}'s process";

        /// <summary>Nothing running, nothing kept loaded, and unused for <paramref name="after"/>.</summary>
        public bool Idle(TimeSpan after)
        {
            lock (_kept) if (_kept.Values.Any(w => w.Instances.Values.Any(n => n > 0))) return false;
            return _pending.IsEmpty && Environment.TickCount64 - Interlocked.Read(ref _lastUsed) > after.TotalMilliseconds;
        }

        private async Task EnsureStartedAsync(CancellationToken ct)
        {
            if (_process is { HasExited: false } && _ready is { Task.IsCompletedSuccessfully: true }) return;
            var restarted = false;
            await _start.WaitAsync(ct);
            try
            {
                if (_process is { HasExited: false } && _ready is { Task.IsCompletedSuccessfully: true }) return;
                var deno = Locate(runtime._options);
                var script = Path.Combine(WorkFolder("runtime"), "runtime.js");
                await File.WriteAllTextAsync(script, Resource("runtime.js"), ct);
                var info = new ProcessStartInfo(deno)
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                    StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
                };
                // Reading: only the bundles and resources folders (each worker gets its own files of them, nothing else).
                var readable = $"--allow-read={WorkFolder("bundles")},{WorkFolder("resources")}";
                // --expose-gc: an instance over its memory after a run has its garbage collected before it's measured (the
                // runtime keeps gc() to itself). --max-old-space-size: no worker's heap grows past the process's limit; one
                // that tries ends this process (its runs fail as out of memory), not the server.
                var v8 = $"--v8-flags=--expose-gc,--max-old-space-size={heapMb}";
                foreach (var a in new[] { "run", "--quiet", "--no-prompt", "--unstable-worker-options", v8, "--allow-net", "--allow-import", readable, script })
                    info.ArgumentList.Add(a);
                info.Environment["NO_COLOR"] = "1";
                if (runtime._options.CachePath is { } cache) info.Environment["DENO_DIR"] = cache;
                lock (_sent) _sent.Clear();
                _outOfMemory = false;
                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _process = System.Diagnostics.Process.Start(info) ?? throw new FunctionsUnavailableException("Deno didn't start.");
                var process = _process;
                _ = Task.Run(() => ReadAsync(process));
                _ = Task.Run(async () =>
                {
                    while (await process.StandardError.ReadLineAsync() is { } line)
                    {
                        if (line.Contains("out of memory", StringComparison.OrdinalIgnoreCase)) _outOfMemory = true;
                        runtime._logger.LogWarning("Functions runtime ({Process}): {Line}", Label, line);
                    }
                });
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(30));
                try { await _ready.Task.WaitAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new FunctionsUnavailableException("The functions runtime didn't start within 30 seconds."); }
                runtime._logger.LogInformation("Functions runtime started: {Process} (Deno, pid {Pid}, {HeapMb} MB per worker).", Label, process.Id, heapMb);
                restarted = _starts++ > 0;
                StartedAt = DateTimeOffset.UtcNow;
            }
            finally { _start.Release(); }
            // Started again after a crash: the instances it kept loaded are loaded again.
            if (restarted) _ = Task.Run(RewarmAsync);
        }

        private async Task RewarmAsync()
        {
            List<Warmth> kept;
            lock (_kept) kept = [.. _kept.Values];
            foreach (var w in kept)
            {
                try { await WarmAsync(w.Version, w.Code, w.Resources, w.Instances, CancellationToken.None); }
                catch (Exception e) { runtime._logger.LogDebug(e, "{Process}: instances not loaded again after a restart.", Label); }
            }
        }

        private async Task ReadAsync(Process process)
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    JsonObject msg;
                    try { msg = JsonNode.Parse(line)!.AsObject(); }
                    catch (Exception) { continue; }
                    var t = msg["t"]?.GetValue<string>();
                    if (t == "ready") { _ready?.TrySetResult(); continue; }
                    var id = msg["id"]?.GetValue<long>() ?? -1;
                    if (!_pending.TryGetValue(id, out var p)) continue;
                    switch (t)
                    {
                        case "log":
                            p.Log?.Invoke(msg["level"]?.GetValue<string>() ?? "log", msg["message"]?.GetValue<string>() ?? "");
                            break;
                        case "call":
                            _ = Task.Run(() => AnswerAsync(id, msg, p));
                            break;
                        case "done":
                            if (_pending.TryRemove(id, out var done)) done.Done.TrySetResult(msg);
                            break;
                    }
                }
            }
            finally
            {
                try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token); } catch (Exception) { }
                // V8 says so on stderr; give that reader a moment to see it.
                for (var i = 0; i < 20 && !_outOfMemory && process.HasExited && process.ExitCode != 0; i++) await Task.Delay(25);
                var error = _outOfMemory
                    ? Error("resource-exhausted", $"Ran out of memory: a run in {Label} went past its limit of {heapMb} MB, which stopped every run there. It starts again on the next run.")
                    : Error("internal", "The functions runtime stopped; it starts again on the next run.");
                foreach (var (id, p) in _pending)
                    if (_pending.TryRemove(id, out _)) p.Done.TrySetResult(error);
                _ready?.TrySetException(new FunctionsUnavailableException("The functions runtime stopped."));
                if (!process.HasExited) { try { process.Kill(); } catch (Exception) { } }
                if (_disposing) runtime._logger.LogInformation("Functions runtime stopped: {Process}.", Label);
                else runtime._logger.LogWarning("Functions runtime stopped: {Process} (exit {Code}){Why}.", Label, process.HasExited ? process.ExitCode : -1,
                    _outOfMemory ? $", out of memory ({heapMb} MB per worker)" : "");
                if (!_disposing)
                    runtime.Note(new FunctionEvent(DateTimeOffset.UtcNow, name == Shared ? null : name, name == Shared ? null : name,
                        _outOfMemory ? "out-of-memory" : "stopped",
                        _outOfMemory ? $"{char.ToUpperInvariant(Label[0])}{Label[1..]} ran out of memory ({heapMb} MB per worker); its runs in flight failed."
                            : $"{char.ToUpperInvariant(Label[0])}{Label[1..]} stopped (exit {(process.HasExited ? process.ExitCode : -1)}); its runs in flight failed."));
            }
        }

        private async Task AnswerAsync(long id, JsonObject msg, Pending p)
        {
            var reply = new JsonObject { ["t"] = "reply", ["id"] = id, ["callId"] = msg["callId"]?.DeepClone() };
            try
            {
                if (p.Bridge is null) throw new FunctionCallException("unavailable", "ctx isn't available here.");
                reply["result"] = await p.Bridge(msg["op"]!.GetValue<string>(), msg["args"] as JsonObject ?? []);
            }
            catch (FunctionCallException ex) { reply["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message }; }
            catch (Exception ex) { reply["error"] = new JsonObject { ["code"] = "internal", ["message"] = ex.GetBaseException().Message }; }
            try { await SendAsync(reply, CancellationToken.None); }
            catch (Exception) { /* the process stopped: its runs are failed by the reader */ }
        }

        private async Task SendAsync(JsonObject msg, CancellationToken ct)
        {
            await _write.WaitAsync(ct);
            try
            {
                var p = _process ?? throw new FunctionsUnavailableException("The functions runtime isn't running.");
                await p.StandardInput.WriteLineAsync(msg.ToJsonString().AsMemory(), ct);
                await p.StandardInput.FlushAsync(ct);
            }
            finally { _write.Release(); }
        }

        /// <summary>Gives this process a version's bundle file and its resources' files (once per version).</summary>
        private async Task EnsureCodeAsync(string version, string code, Func<Task<IReadOnlyDictionary<string, string>>>? resources, CancellationToken ct)
        {
            lock (_sent) if (_sent.Contains(version)) return;
            var file = await runtime.BundleFileAsync(version, code, ct);
            var files = resources is null ? new Dictionary<string, string>() : await resources();
            await SendAsync(new JsonObject
            {
                ["t"] = "code", ["version"] = version, ["url"] = new Uri(file).AbsoluteUri, ["file"] = file,
                ["resources"] = new JsonObject(files.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            }, ct);
            lock (_sent) _sent.Add(version);
        }

        public async Task<JsonObject> RequestAsync(JsonObject msg, string version, string code, Pending pending, CancellationToken ct,
            Func<Task<IReadOnlyDictionary<string, string>>>? resources = null)
        {
            Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);
            await EnsureStartedAsync(ct);
            await EnsureCodeAsync(version, code, resources, ct);
            var id = Interlocked.Increment(ref runtime._next);
            msg["id"] = id;
            _pending[id] = pending;
            try { await SendAsync(msg, ct); }
            catch { _pending.TryRemove(id, out _); throw; }
            using (ct.Register(() => { if (_pending.TryRemove(id, out var p)) p.Done.TrySetResult(Error("cancelled", "The run was cancelled.")); }))
                return await pending.Done.Task;
        }

        public async Task WarmAsync(string version, string code, Func<Task<IReadOnlyDictionary<string, string>>>? resources,
            IReadOnlyDictionary<string, int> instances, CancellationToken ct)
        {
            lock (_kept) _kept[version] = new Warmth(version, code, resources, instances);
            Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);
            await EnsureStartedAsync(ct);
            await EnsureCodeAsync(version, code, resources, ct);
            // Answered once the instances are loaded (a server is ready only then).
            await AskAsync(new JsonObject
            {
                ["t"] = "warm", ["version"] = version,
                ["functions"] = new JsonObject(instances.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            }, ct);
        }

        public async Task ForgetAsync(string version, CancellationToken ct)
        {
            bool had;
            lock (_sent) had = _sent.Remove(version);
            lock (_kept) _kept.Remove(version);
            if (had && Running)
            {
                try { await SendAsync(new JsonObject { ["t"] = "forget", ["version"] = version }, ct); }
                catch (Exception) { /* stopped meanwhile: nothing to forget */ }
            }
        }

        private volatile bool _disposing;

        /// <summary>Asks the running process something answered as done (stats); doesn't start it.</summary>
        public async Task<JsonObject> AskAsync(JsonObject msg, CancellationToken ct)
        {
            if (!Running) throw new FunctionsUnavailableException("Not running.");
            var id = Interlocked.Increment(ref runtime._next);
            msg["id"] = id;
            var pending = new Pending(new(TaskCreationOptions.RunContinuationsAsynchronously), null, null);
            _pending[id] = pending;
            try { await SendAsync(msg, ct); }
            catch { _pending.TryRemove(id, out _); throw; }
            using (ct.Register(() => { if (_pending.TryRemove(id, out var p)) p.Done.TrySetCanceled(); }))
                return await pending.Done.Task;
        }

        public async ValueTask DisposeAsync()
        {
            _disposing = true;
            if (_process is { HasExited: false } p)
            {
                try { p.StandardInput.Close(); await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); }
                catch (Exception) { try { p.Kill(); } catch (Exception) { } }
            }
            _process?.Dispose();
        }
    }
}
