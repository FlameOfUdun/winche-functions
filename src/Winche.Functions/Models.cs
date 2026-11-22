using System.Text.Json.Nodes;

namespace Winche.Functions;

/// <summary>What starts a function: Kind is documents, files, users, schedule or callable.</summary>
/// <param name="On">For before-functions (Kind before): documents, files or users.</param>
public sealed record FunctionTrigger(string Kind, IReadOnlyList<string>? Events = null, string? Path = null, string? Cron = null, string? TimeZone = null,
    string? On = null)
{
    public string Describe() => Kind switch
    {
        "documents" => $"documents {string.Join("/", Events ?? [])} at {Path}",
        "files" => $"files {string.Join("/", Events ?? [])} at {Path}",
        "users" => $"users {string.Join("/", Events ?? [])}",
        "assets" => $"assets {string.Join("/", Events ?? [])}",
        "schedule" => $"schedule {Cron}{(TimeZone is null ? " (UTC)" : $" ({TimeZone})")}",
        "before" => $"before {On} {string.Join("/", Events ?? [])}{(Path is null ? "" : $" at {Path}")}",
        "consolePage" => "a console page",
        "http" => $"HTTP {(Events is { Count: > 0 } m ? string.Join("/", m) : "any method")} at /functions/v1/http/{{name}}",
        _ => "called by apps",
    };
}

/// <summary>An exported function of a deployment.</summary>
/// <param name="FailOpen">Before-functions: let the write through when the function crashes or times out.</param>
/// <param name="Secrets">The secrets it lists (options.secrets: handles from secrets.define), which it may read.</param>
/// <param name="MinRole">Console pages: the lowest console role that may see it (viewer, member, admin).</param>
/// <param name="Page">Console pages: what the console shows.</param>
/// <param name="CodeTimeoutSeconds">The run time its code sets (options.timeoutSeconds), if it does.</param>
/// <param name="About">What it does, in a sentence (options.description).</param>
/// <param name="Input">The shape of what it takes, as JSON Schema (options.input): shown, not checked.</param>
/// <param name="Output">The shape of what it returns, as JSON Schema (options.output): shown, not checked.</param>
/// <param name="Example">A sample input (options.example); without one, made from <see cref="Input"/> (<see cref="ExampleInput"/>).</param>
/// <param name="Fresh">A new sandbox for every run (options.fresh), instead of a warm worker reused from run to run.</param>
public sealed record FunctionInfo(string Name, FunctionTrigger Trigger, int TimeoutSeconds, bool FailOpen = false, IReadOnlyList<string>? Secrets = null,
    string? MinRole = null, ConsolePageInfo? Page = null, int? CodeTimeoutSeconds = null,
    string? About = null, JsonNode? Input = null, JsonNode? Output = null, JsonNode? Example = null, bool Fresh = false)
{
    /// <summary>What its code says about its instances (memory, how many); the settings can say otherwise.</summary>
    public FunctionLimits Limits { get; init; } = new();

    /// <summary>The plugin copy it belongs to (its id), or null for the project's own code.</summary>
    public string? Plugin { get; init; }

    /// <summary>
    /// A callable apps may call only with a valid Integrity token (options.integrity), whatever the project's
    /// enforcement; the project's own code calls it as ever.
    /// </summary>
    public bool RequireIntegrity { get; init; }

    /// <summary>The input to start from when running it by hand: its example, else one made from its input schema.</summary>
    public JsonNode? ExampleInput => Example?.DeepClone() ?? (Input is JsonObject schema ? SchemaExample.Of(schema) : null);
}

/// <summary>
/// How a function's instances may run (workers with its code loaded, one run at a time), as its code or a setting
/// says; null where it doesn't.
/// </summary>
/// <param name="MemoryMb">The most an instance may hold after a run; past it, the instance is replaced.</param>
/// <param name="MinInstances">Instances kept loaded even when idle.</param>
/// <param name="MaxInstances">The most runs at once; more wait their turn.</param>
/// <param name="ReservedInstances">Runs of the project's limit kept for this function.</param>
/// <param name="Isolate">A process of its own, so nothing it does stops other functions' runs.</param>
public sealed record FunctionLimits(int? MemoryMb = null, int? MinInstances = null, int? MaxInstances = null, int? ReservedInstances = null, bool? Isolate = null);

/// <summary>The limits in force for a function, and where each comes from: <c>setting</c>, <c>code</c> or <c>default</c>.</summary>
public sealed record FunctionLimitsInForce(int MemoryMb, int MinInstances, int MaxInstances, int ReservedInstances, bool Isolate,
    IReadOnlyDictionary<string, string> From);

/// <summary>A console page: its sidebar title and icon, and the folder of its files in the deployment.</summary>
public sealed record ConsolePageInfo(string Title, string Files, string? Icon, int Order);

/// <summary>A deployed version: its source files, the bundle that runs, and the functions found in it.</summary>
/// <param name="Resources">The files of its resources folder (ctx.resources), by path: each one's content hash and size.</param>
/// <param name="Plugins">The plugins installed in it (their functions are among <paramref name="Functions"/>, named id.name).</param>
public sealed record Deployment(int Version, IReadOnlyDictionary<string, string> Files, string Bundle, IReadOnlyList<FunctionInfo> Functions,
    string? Note, string? By, DateTimeOffset At, int? RolledBackFrom, IReadOnlyDictionary<string, ResourceEntry>? Resources = null,
    IReadOnlyList<PluginInstall>? Plugins = null)
{
    public IReadOnlyDictionary<string, ResourceEntry> ResourceFiles => Resources ?? new Dictionary<string, ResourceEntry>();

    /// <summary>Its plugins (none when null).</summary>
    public IReadOnlyList<PluginInstall> PluginList => Plugins ?? [];
}

/// <summary>An resource of a deployment: its content's SHA-256 (lowercase hex) and its size in bytes.</summary>
public sealed record ResourceEntry(string Hash, long Size);

/// <summary>A deployment that couldn't be made, and why (bundling errors, problems with triggers).</summary>
public sealed class FunctionDeployException(string message, IReadOnlyList<string> problems) : Exception(message)
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>How a run went: the result, or an error code and message; the lines it logged.</summary>
public sealed record FunctionResult(bool Ok, JsonNode? Result, string? ErrorCode, string? ErrorMessage, bool PublicError, int DurationMs,
    IReadOnlyList<FunctionLogLine> Logs)
{
    /// <summary>What an <c>HttpsError</c> sent as its <c>details</c>, for the app.</summary>
    public JsonNode? ErrorDetails { get; init; }

    public static FunctionResult Failed(string code, string message) => new(false, null, code, message, false, 0, []);
}

public sealed record FunctionLogLine(string Level, string Message, DateTimeOffset At);

/// <summary>Who's calling: for callables, the caller's claims.</summary>
public sealed record FunctionCaller(string? Uid, IReadOnlyDictionary<string, object?>? Token)
{
    /// <summary>The app that proved it's the project's own for this request (Integrity), if one did: <c>ctx.app</c>.</summary>
    public FunctionAppProof? App { get; init; }

    /// <summary>An app's request (a callable called by an app, an HTTP function's request): never the server's own call.</summary>
    public bool FromApp { get; init; }

    /// <summary>The server's own call (another function, the console, a server with the admin SDK), not an app's nor a user's.</summary>
    public bool IsServer => !FromApp && Uid is null;

    public static readonly FunctionCaller Server = new(null, null);
}

/// <summary>An app's proof that it's the project's own (Integrity): its id, and how it proved itself (appAttest, …).</summary>
public sealed record FunctionAppProof(string Id, string Provider);

/// <summary>A service functions reach through ctx (db, files, users), from a package's adapter.</summary>
public interface IFunctionBinding
{
    /// <summary>The part before the dot: db, files or users.</summary>
    string Name { get; }

    /// <summary>Does <paramref name="op"/> (e.g. get) with the server's rights; a <see cref="FunctionCallException"/> goes back to the function.</summary>
    Task<JsonNode?> CallAsync(string op, JsonObject args, IServiceProvider services, CancellationToken ct);
}

/// <summary>A failure a function sees as an error with a code (e.g. not-found, invalid-argument).</summary>
public sealed class FunctionCallException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Something that happened to a function's instance or a process: <c>memory</c>, <c>timeout</c> or <c>crash</c> (an
/// instance replaced), <c>out-of-memory</c> or <c>stopped</c> (a process; its runs in flight failed).
/// </summary>
/// <param name="Function">The function, or null for the shared process.</param>
/// <param name="Process">The process (an isolated function's name), or null for the shared one.</param>
public sealed record FunctionEvent(DateTimeOffset At, string? Function, string? Process, string Kind, string Message);

/// <summary>A function's instance now: <c>busy</c>, <c>idle</c> or <c>loading</c>; its heap after its last run.</summary>
public sealed record FunctionInstanceInfo(string Function, string State, int? HeapMb, int Runs, int AgeSeconds, int? BusySeconds);

/// <summary>A process of the project: the shared one (Function null) or an isolated function's.</summary>
public sealed record FunctionProcessInfo(string? Function, bool Running, int? Pid, int HeapMb, DateTimeOffset? StartedAt, int Starts,
    IReadOnlyList<FunctionInstanceInfo> Instances);
