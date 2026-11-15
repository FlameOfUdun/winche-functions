using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Winche.Functions;

/// <summary>
/// Runs the deployed before-functions for a write, in the order they're exported: each sees the write with the earlier
/// ones' changes, and may refuse it (an HttpsError, whose message reaches the caller) or return changes. A function
/// that crashes or runs out of time refuses the write, unless it's <c>failOpen</c>. Used by Winche.Functions.Database,
/// .Storage and .Auth.
/// </summary>
public sealed class BeforeFunctions(WincheFunctions functions, ILoggerFactory loggers)
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5), MaxTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger _log = loggers.CreateLogger(WincheFunctions.LogCategory);

    /// <summary>Whether any before-function is deployed for <paramref name="on"/> (documents, files, users).</summary>
    public bool Any(string on) => functions.Current?.Functions.Any(f => f.Trigger.Kind == "before" && f.Trigger.On == on) == true;

    /// <summary>
    /// Runs those for <paramref name="on"/> and <paramref name="event"/> (whose path pattern, if any, matches
    /// <paramref name="path"/>): <paramref name="argument"/> makes each one's event from the pattern's captures,
    /// <paramref name="apply"/> takes what each returns. Throws <see cref="WincheDeniedException"/> to refuse.
    /// </summary>
    public async Task RunAsync(string on, string @event, string? path, Func<JsonObject, JsonObject> argument, FunctionCaller caller,
        Action<JsonNode?> apply, CancellationToken ct)
    {
        foreach (var fn in functions.Current?.Functions ?? [])
        {
            if (fn.Trigger is not { Kind: "before" } t || t.On != on || t.Events?.Contains(@event) != true) continue;
            var captures = new JsonObject();
            if (t.Path is { } p)
            {
                if (path is null || !PathPattern.TryParse(p, out var pattern, out _) || pattern!.Match(path) is not { } m) continue;
                foreach (var (k, v) in m) captures[k] = v;
            }
            var result = await functions.InvokeAsync(fn.Name, argument(captures), caller, ct, "before", readOnly: true);
            if (result.Ok)
            {
                apply(result.Result);
                continue;
            }
            if (result.PublicError) throw new WincheDeniedException(result.ErrorMessage ?? "Refused.");
            if (fn.FailOpen)
            {
                _log.LogWarning("Before-function {Function} failed ({Code}); the write went through (failOpen).", fn.Name, result.ErrorCode);
                continue;
            }
            throw new WincheDeniedException($"This change couldn't be checked ({fn.Name} failed), so it wasn't saved.");
        }
    }

    /// <summary>The caller as functions see it, from a request's claims.</summary>
    public static FunctionCaller Caller(IReadOnlyDictionary<string, object?>? claims) =>
        claims?.GetValueOrDefault("uid") is string uid ? new FunctionCaller(uid, claims) : FunctionCaller.Server;

    /// <summary>The <c>auth</c> of a before-event.</summary>
    public static JsonNode? Auth(FunctionCaller caller) => caller.Uid is null ? null
        : new JsonObject { ["uid"] = caller.Uid, ["token"] = System.Text.Json.JsonSerializer.SerializeToNode(caller.Token ?? new Dictionary<string, object?>()) };
}
