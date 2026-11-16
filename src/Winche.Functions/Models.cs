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
        "consoleAction" => "run from console pages",
        _ => "called by apps",
    };
}

/// <summary>An exported function of a deployment.</summary>
/// <param name="FailOpen">Before-functions: let the write through when the function crashes or times out.</param>
/// <param name="Secrets">The secrets it declared (options.secrets), which it reads with ctx.secrets.get.</param>
/// <param name="MinRole">Console actions and pages: the lowest console role that may run or see it (viewer, member, admin).</param>
/// <param name="Page">Console pages: what the console shows.</param>
/// <param name="CodeTimeoutSeconds">The run time its code sets (options.timeoutSeconds), if it does.</param>
/// <param name="About">What it does, in a sentence (options.description).</param>
/// <param name="Input">The shape of what it takes, as JSON Schema (options.input): shown, not checked.</param>
/// <param name="Output">The shape of what it returns, as JSON Schema (options.output): shown, not checked.</param>
/// <param name="Example">A sample input (options.example); without one, made from <see cref="Input"/> (<see cref="ExampleInput"/>).</param>
public sealed record FunctionInfo(string Name, FunctionTrigger Trigger, int TimeoutSeconds, bool FailOpen = false, IReadOnlyList<string>? Secrets = null,
    string? MinRole = null, ConsolePageInfo? Page = null, int? CodeTimeoutSeconds = null,
    string? About = null, JsonNode? Input = null, JsonNode? Output = null, JsonNode? Example = null)
{
    /// <summary>The input to start from when running it by hand: its example, else one made from its input schema.</summary>
    public JsonNode? ExampleInput => Example?.DeepClone() ?? (Input is JsonObject schema ? SchemaExample.Of(schema) : null);
}

/// <summary>A console page: its sidebar title and icon, and the folder of its files in the deployment.</summary>
public sealed record ConsolePageInfo(string Title, string Files, string? Icon, int Order);

/// <summary>A deployed version: its source files, the bundle that runs, and the functions found in it.</summary>
/// <param name="Resources">The files of its resources folder (ctx.resources), by path: each one's content hash and size.</param>
public sealed record Deployment(int Version, IReadOnlyDictionary<string, string> Files, string Bundle, IReadOnlyList<FunctionInfo> Functions,
    string? Note, string? By, DateTimeOffset At, int? RolledBackFrom, IReadOnlyDictionary<string, ResourceEntry>? Resources = null)
{
    public IReadOnlyDictionary<string, ResourceEntry> ResourceFiles => Resources ?? new Dictionary<string, ResourceEntry>();
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
    public static readonly FunctionCaller Server = new(null, null);
}

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
