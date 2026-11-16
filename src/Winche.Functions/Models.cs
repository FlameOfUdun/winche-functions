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
        "schedule" => $"schedule {Cron}{(TimeZone is null ? " (UTC)" : $" ({TimeZone})")}",
        "before" => $"before {On} {string.Join("/", Events ?? [])}{(Path is null ? "" : $" at {Path}")}",
        _ => "called by apps",
    };
}

/// <summary>An exported function of a deployment.</summary>
/// <param name="FailOpen">Before-functions: let the write through when the function crashes or times out.</param>
/// <param name="Secrets">The secrets it declared (options.secrets), which it reads with ctx.secrets.get.</param>
public sealed record FunctionInfo(string Name, FunctionTrigger Trigger, int TimeoutSeconds, bool FailOpen = false, IReadOnlyList<string>? Secrets = null);

/// <summary>A deployed version: its source files, the bundle that runs, and the functions found in it.</summary>
public sealed record Deployment(int Version, IReadOnlyDictionary<string, string> Files, string Bundle, IReadOnlyList<FunctionInfo> Functions,
    string? Note, string? By, DateTimeOffset At, int? RolledBackFrom);

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
