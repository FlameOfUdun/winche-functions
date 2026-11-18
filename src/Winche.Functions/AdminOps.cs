using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Winche.Functions;

/// <summary>
/// The operations @winche/admin sends (<c>binding.op</c> with JSON arguments), run by the bindings. Functions run them
/// with the server's rights; the console's admin API with the caller's role. An <c>as</c> argument (from
/// <c>as(user)</c>) makes documents and files calls act as one of the app's users, under the access rules.
/// </summary>
public static class AdminOps
{
    private static readonly HashSet<string> Reads =
    [
        "db.get", "db.getAll", "db.query", "db.count", "db.aggregate",
        "files.get", "files.read", "files.page", "files.list", "files.downloadUrl",
        "users.get",
        "assets.channels", "assets.channel", "assets.release", "assets.releases", "assets.read", "assets.downloadUrl",
    ];

    /// <summary>Whether <paramref name="op"/> only reads.</summary>
    public static bool IsRead(string op) => Reads.Contains(op);

    /// <summary>Whether <paramref name="op"/> changes the project's data (documents, files, the app's users) rather than reading it.</summary>
    public static bool IsDataWrite(string op) => !IsRead(op) && (op.StartsWith("db.", StringComparison.Ordinal) || op.StartsWith("files.", StringComparison.Ordinal) || op.StartsWith("users.", StringComparison.Ordinal));

    /// <summary>
    /// Runs <paramref name="op"/> with <paramref name="args"/> (an <c>as</c> argument, if any, taken out and applied) in a
    /// scope of its own; failures are <see cref="FunctionCallException"/>s with a code.
    /// </summary>
    public static async Task<JsonNode?> RunAsync(IServiceProvider services, string op, JsonObject args, CancellationToken ct)
    {
        var dot = op.IndexOf('.');
        var name = dot > 0 ? op[..dot] : op;
        var binding = services.GetServices<IFunctionBinding>().FirstOrDefault(b => b.Name == name)
            ?? throw new FunctionCallException("unavailable", $"@winche/admin's {name} isn't available on this server.");
        args = (JsonObject)args.DeepClone();
        var asUser = args["as"] is JsonObject who ? Caller(who) : null;
        args.Remove("as");
        if (asUser is not null && binding is not IActsAsAppUser)
            throw new FunctionCallException("invalid-argument", $"as(user) covers documents, files and functions; {name} has no access rules for app users.");
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AdminCaller>().As = asUser;
        return await binding.CallAsync(op[(dot + 1)..], args, scope.ServiceProvider, ct);
    }

    private static FunctionCaller Caller(JsonObject who)
    {
        var uid = who["uid"]?.GetValue<string>() is { Length: > 0 } u ? u : throw new FunctionCallException("invalid-argument", "as(user): the user has no uid.");
        var token = who["token"] is JsonObject t ? JsonSerializer.Deserialize<Dictionary<string, object?>>(t.ToJsonString()) : null;
        return new FunctionCaller(uid, token ?? new Dictionary<string, object?> { ["sub"] = uid });
    }
}

/// <summary>
/// Who an admin call acts as, for one call's scope: null for the caller's own rights (the server's in functions), or an
/// app user from <c>as(user)</c>, whose access rules then apply.
/// </summary>
public sealed class AdminCaller
{
    public FunctionCaller? As { get; set; }
}

/// <summary>
/// A binding that can act as one of the app's users (<see cref="AdminCaller.As"/>): documents and files check their
/// access rules for them, functions.call passes them as the caller. Others refuse <c>as(user)</c>.
/// </summary>
public interface IActsAsAppUser;

/// <summary>functions.call: runs a callable (as the app user given with as(user), else with no caller) and returns its result.</summary>
internal sealed class FunctionsCallBinding : IFunctionBinding, IActsAsAppUser
{
    public string Name => "functions";

    public async Task<JsonNode?> CallAsync(string op, JsonObject args, IServiceProvider services, CancellationToken ct)
    {
        if (op != "call") throw new FunctionCallException("invalid-argument", $"functions: '{op}' isn't an operation.");
        var name = args["name"]?.GetValue<string>() is { Length: > 0 } n ? n : throw new FunctionCallException("invalid-argument", "Give the callable's name.");
        var caller = services.GetRequiredService<AdminCaller>().As ?? FunctionCaller.Server;
        var result = await services.GetRequiredService<WincheFunctions>().InvokeAsync(name, args["data"]?.DeepClone(), caller, ct, kind: "callable");
        if (result.Ok) return result.Result;
        throw new FunctionCallException(result.PublicError || result.ErrorCode is "not-found" ? result.ErrorCode ?? "internal" : "internal",
            result.PublicError || result.ErrorCode is "not-found" ? result.ErrorMessage ?? "The function failed." : $"{name} failed (its logs say why).");
    }
}
