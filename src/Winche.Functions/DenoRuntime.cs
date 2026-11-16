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
/// The Deno process that runs functions: started on first use, restarted if it dies. Each run is a sandboxed worker in
/// it (network only); requests and answers are JSON lines on its stdin and stdout.
/// </summary>
public sealed class DenoRuntime(FunctionsOptions options, ILogger<DenoRuntime> logger) : IAsyncDisposable
{
    private sealed record Pending(TaskCompletionSource<JsonObject> Done, Func<string, JsonObject, Task<JsonNode?>>? Bridge, Action<string, string>? Log);

    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _running = new(Math.Max(1, options.MaxConcurrency));
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly HashSet<string> _sent = [];
    private readonly Dictionary<string, string> _bundleFiles = [];   // version -> its bundle's file
    private Process? _process;
    private TaskCompletionSource? _ready;
    private long _next;

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

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false } && _ready is { Task.IsCompletedSuccessfully: true }) return;
        await _start.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false } && _ready is { Task.IsCompletedSuccessfully: true }) return;
            var deno = Locate(options);
            var script = Path.Combine(WorkFolder("runtime"), "runtime.js");
            await File.WriteAllTextAsync(script, Resource("runtime.js"), ct);
            var info = new ProcessStartInfo(deno)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
            };
            // Reading: only the bundles and resources folders (each worker gets its own files of them, nothing else).
            var readable = $"--allow-read={WorkFolder("bundles")},{WorkFolder("resources")}";
            foreach (var a in new[] { "run", "--quiet", "--no-prompt", "--unstable-worker-options", "--allow-net", "--allow-import", readable, script }) info.ArgumentList.Add(a);
            info.Environment["NO_COLOR"] = "1";
            if (options.CachePath is { } cache) info.Environment["DENO_DIR"] = cache;
            lock (_sent) { _sent.Clear(); _bundleFiles.Clear(); }
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _process = Process.Start(info) ?? throw new FunctionsUnavailableException("Deno didn't start.");
            var process = _process;
            _ = Task.Run(() => ReadAsync(process));
            _ = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line) logger.LogWarning("Functions runtime: {Line}", line);
            });
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            try { await _ready.Task.WaitAsync(wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new FunctionsUnavailableException("The functions runtime didn't start within 30 seconds."); }
            logger.LogInformation("Functions runtime started (Deno, pid {Pid}).", process.Id);
        }
        finally { _start.Release(); }
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
            foreach (var (id, p) in _pending)
                if (_pending.TryRemove(id, out _)) p.Done.TrySetResult(Error("internal", "The functions runtime stopped; it starts again on the next run."));
            _ready?.TrySetException(new FunctionsUnavailableException("The functions runtime stopped."));
            if (!process.HasExited) { try { process.Kill(); } catch (Exception) { } }
            logger.LogWarning("Functions runtime stopped (exit {Code}).", process.HasExited ? process.ExitCode : -1);
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
        await SendAsync(reply, CancellationToken.None);
    }

    private static JsonObject Error(string code, string message) =>
        new() { ["t"] = "done", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

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

    /// <summary>
    /// Gives the runtime a version's bundle as a file named by its content (so Deno compiles it once and reuses its code
    /// cache on every run, where a data: URL was compiled again each time) and its resources' files.
    /// </summary>
    private async Task EnsureCodeAsync(string version, string code, Func<Task<IReadOnlyDictionary<string, string>>>? resources, CancellationToken ct)
    {
        lock (_sent) if (_sent.Contains(version)) return;
        var file = Path.Combine(WorkFolder("bundles"), WincheFunctions.ContentHash(Encoding.UTF8.GetBytes(code)) + ".js");
        if (!File.Exists(file))
        {
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".part";
            await File.WriteAllTextAsync(temp, code, new UTF8Encoding(false), ct);
            File.Move(temp, file, overwrite: true);
        }
        var files = resources is null ? new Dictionary<string, string>() : await resources();
        await SendAsync(new JsonObject
        {
            ["t"] = "code", ["version"] = version, ["url"] = new Uri(file).AbsoluteUri, ["file"] = file,
            ["resources"] = new JsonObject(files.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
        }, ct);
        lock (_sent) { _sent.Add(version); _bundleFiles[version] = file; }
    }

    private async Task<JsonObject> RequestAsync(JsonObject msg, string version, string code, Pending pending, CancellationToken ct,
        Func<Task<IReadOnlyDictionary<string, string>>>? resources = null)
    {
        await EnsureStartedAsync(ct);
        await EnsureCodeAsync(version, code, resources, ct);
        var id = Interlocked.Increment(ref _next);
        msg["id"] = id;
        _pending[id] = pending;
        try { await SendAsync(msg, ct); }
        catch { _pending.TryRemove(id, out _); throw; }
        using (ct.Register(() => { if (_pending.TryRemove(id, out var p)) p.Done.TrySetResult(Error("cancelled", "The run was cancelled.")); }))
            return await pending.Done.Task;
    }

    /// <summary>Drops a bundle from the runtime (an analyzed one, or an old version).</summary>
    public async Task ForgetAsync(string version, CancellationToken ct)
    {
        string? unused = null;
        lock (_sent)
        {
            if (!_sent.Remove(version)) return;
            // Its bundle file goes too, unless another version in use has the same code.
            if (_bundleFiles.Remove(version, out var file) && !_bundleFiles.ContainsValue(file)) unused = file;
        }
        await SendAsync(new JsonObject { ["t"] = "forget", ["version"] = version }, ct);
        if (unused is not null) { try { File.Delete(unused); } catch (IOException) { } }
    }

    /// <summary>The exported functions of a bundle and their triggers (run in a worker without network).</summary>
    public async Task<JsonArray> AnalyzeAsync(string version, string code, CancellationToken ct)
    {
        var done = await RequestAsync(new JsonObject { ["t"] = "analyze", ["version"] = version }, version, code,
            new Pending(new(TaskCreationOptions.RunContinuationsAsynchronously), null, null), ct);
        if (done["error"] is JsonObject e) throw new FunctionDeployException("The code doesn't load.", [e["message"]?.GetValue<string>() ?? "It failed to load."]);
        return done["result"] as JsonArray ?? [];
    }

    /// <summary>Runs function <paramref name="name"/>; <paramref name="bridge"/> answers its ctx calls, <paramref name="log"/> gets its output.</summary>
    public async Task<JsonObject> InvokeAsync(string version, string code, string name, JsonNode? arg, JsonObject context, TimeSpan timeout,
        Func<string, JsonObject, Task<JsonNode?>> bridge, Action<string, string> log, CancellationToken ct,
        Func<Task<IReadOnlyDictionary<string, string>>>? resources = null)
    {
        await _running.WaitAsync(ct);
        try
        {
            return await RequestAsync(new JsonObject
            {
                ["t"] = "invoke", ["version"] = version, ["name"] = name, ["arg"] = arg?.DeepClone(), ["context"] = context,
                ["timeoutMs"] = (long)timeout.TotalMilliseconds,
            }, version, code, new Pending(new(TaskCreationOptions.RunContinuationsAsynchronously), bridge, log), ct, resources);
        }
        finally { _running.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false } p)
        {
            try { p.StandardInput.Close(); await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); }
            catch (Exception) { try { p.Kill(); } catch (Exception) { } }
        }
        _process?.Dispose();
    }
}
