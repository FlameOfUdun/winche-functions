using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Auth;

namespace Winche.Functions.Auth;

public static class AuthFunctionsExtensions
{
    /// <summary>
    /// Gives functions <c>ctx.users</c> (read Winche Auth users and set their custom claims), and runs
    /// <c>users.beforeCreate</c> / <c>users.beforeSignIn</c> functions on sign-ups and sign-ins.
    /// </summary>
    public static IServiceCollection AddWincheFunctionsForAuth(this IServiceCollection services)
    {
        services.AddSingleton<IFunctionBinding, AuthBinding>();
        services.AddSingleton<IWincheAuthGate, FunctionsAuthGate>();
        return services;
    }
}

/// <summary>users.beforeCreate and users.beforeSignIn: the deployed functions refuse a sign-up or a sign-in.</summary>
internal sealed class FunctionsAuthGate(BeforeFunctions functions) : IWincheAuthGate
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task BeforeCreateAsync(AuthAttempt attempt, CancellationToken ct) => !functions.Any("users") ? Task.CompletedTask
        : functions.RunAsync("users", "create", null, _ => new JsonObject
        {
            ["email"] = attempt.Email, ["provider"] = attempt.Provider, ["ipAddress"] = attempt.IpAddress,
        }, FunctionCaller.Server, _ => { }, ct);

    public Task BeforeSignInAsync(WincheUserInfo user, AuthAttempt attempt, CancellationToken ct) => !functions.Any("users") ? Task.CompletedTask
        : functions.RunAsync("users", "signIn", null, _ => new JsonObject
        {
            ["user"] = JsonSerializer.SerializeToNode(user, Json), ["provider"] = attempt.Provider, ["ipAddress"] = attempt.IpAddress,
        }, FunctionCaller.Server, _ => { }, ct);
}

internal sealed class AuthBinding : IFunctionBinding
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Name => "users";

    public async Task<JsonNode?> CallAsync(string op, JsonObject args, IServiceProvider services, CancellationToken ct)
    {
        var auth = services.GetRequiredService<IWincheAuth>();
        var uid = args["uid"]?.GetValue<string>() is { Length: > 0 } u ? u : throw new FunctionCallException("invalid-argument", "Give a uid.");
        try
        {
            switch (op)
            {
                case "get":
                    return await auth.GetUserAsync(uid, ct) is { } user ? JsonSerializer.SerializeToNode(user, Json) : null;
                case "setClaims":
                    var claims = args["claims"] is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)) : null;
                    await auth.SetCustomClaimsAsync(uid, claims, ct);
                    return null;
                default:
                    throw new FunctionCallException("invalid-argument", $"ctx.users.{op} isn't a thing.");
            }
        }
        catch (AuthException ex)
        {
            throw new FunctionCallException(ex.Code == AuthErrorCodes.UserNotFound ? "not-found" : "invalid-argument", ex.Message);
        }
    }

    private static object? Plain(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v => v.ToString(),
        JsonArray a => a.Select(Plain).ToList(),
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)),
        _ => null,
    };
}
