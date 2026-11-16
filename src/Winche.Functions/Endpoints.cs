using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche.Core;

namespace Winche.Functions;

public static class FunctionsEndpoints
{

    public sealed record CallRequest(JsonElement? Data);

    /// <summary>The shared error for a failed run: a code from the shared list, else <c>internal</c> with the code as reason.</summary>
    public static WincheError ErrorOf(FunctionResult result)
    {
        var code = result.ErrorCode ?? "internal";
        // The app sees its own errors (HttpsError) and plain failures; details of internal ones stay in the logs.
        var message = result.PublicError || code is "not-found" or "deadline-exceeded" or "unavailable" ? result.ErrorMessage ?? "" : "The function failed.";
        var details = result.PublicError ? result.ErrorDetails : null;
        return WincheError.IsCode(code)
            ? new WincheError(WincheError.Parse(code), message, details: details is JsonObject o ? o : details is null ? null : new JsonObject { ["value"] = details })
            : new WincheError(WincheErrorCode.Internal, message);
    }

    private static IResult Send(WincheError error) => error.ToResult();

    /// <summary>
    /// Maps the functions API under <paramref name="prefix"/>: <c>POST call/{name}</c> for apps (callable functions, with the
    /// caller's token). Deploying goes through the console (<c>winche functions deploy</c>).
    /// </summary>
    public static IEndpointRouteBuilder MapWincheFunctions(this IEndpointRouteBuilder app, string prefix = "/functions/v1")
    {
        var group = app.MapGroup(prefix);

        group.MapPost("/call/{name}", async (string name, HttpContext http, WincheFunctions functions, FunctionsOptions options, CancellationToken ct) =>
        {
            JsonNode? data = null;
            if (http.Request.ContentLength is > 0 || http.Request.Headers.ContentType.ToString().Contains("json"))
            {
                try { data = (await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct))?["data"]?.DeepClone(); }
                catch (JsonException) { return Send(new WincheError(WincheErrorCode.InvalidArgument, "Send JSON: { \"data\": … }.")); }
            }
            // The request's caller (Winche.Core), or this package's own Claims mapping when it has one.
            var claims = await http.WincheCallerOf(options.Claims);
            var uid = WincheCaller.UidOf(claims);
            var result = await functions.InvokeAsync(name, data, new FunctionCaller(uid, claims), ct, kind: "callable");
            if (result.Ok) return Results.Json(new { data = result.Result });
            return Send(ErrorOf(result));
        });

        // HTTP functions: any request to /http/{name}[/…] runs it with a standard Request and sends back its Response.
        // Open to anyone: the function checks what it needs (a signature, a token in the link).
        group.Map("/http/{name}", (string name, HttpContext http, WincheFunctions functions, FunctionsOptions options, CancellationToken ct) =>
            HttpAsync(name, http, functions, options, ct));
        group.Map("/http/{name}/{**rest}", (string name, HttpContext http, WincheFunctions functions, FunctionsOptions options, CancellationToken ct) =>
            HttpAsync(name, http, functions, options, ct));

        return app;
    }

    // Headers a function's response can't set: the server handles these itself.
    private static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase)
        { "connection", "transfer-encoding", "content-length", "keep-alive", "upgrade", "proxy-connection", "trailer" };

    private static async Task HttpAsync(string name, HttpContext http, WincheFunctions functions, FunctionsOptions options, CancellationToken ct)
    {
        if (functions.Current is null) await functions.RefreshAsync(ct);
        var fn = functions.Current?.Functions.FirstOrDefault(f => f.Name == name && f.Trigger.Kind == "http");
        if (fn is null) { await Send(new WincheError(WincheErrorCode.NotFound, $"There's no HTTP function {name}.")).ExecuteAsync(http); return; }
        if (fn.Trigger.Events is { Count: > 0 } methods && !methods.Contains(http.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            http.Response.Headers.Allow = string.Join(", ", methods);
            await http.Response.WriteAsJsonAsync(new { error = new { code = "invalid-argument", message = $"{name} takes {string.Join(", ", methods)}." } }, ct);
            return;
        }
        if (http.Request.ContentLength > options.MaxHttpRequestBytes)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            body.Write(buffer, 0, read);
            if (body.Length > options.MaxHttpRequestBytes) { http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
        }
        var r = http.Request;
        var request = new JsonObject
        {
            ["url"] = $"{r.Scheme}://{r.Host}{r.PathBase}{r.Path}{r.QueryString}",
            ["method"] = r.Method,
            ["headers"] = new JsonObject(r.Headers.Select(h => KeyValuePair.Create(h.Key.ToLowerInvariant(), (JsonNode?)JsonValue.Create(h.Value.ToString())))),
            ["body"] = body.Length == 0 ? null : Convert.ToBase64String(body.GetBuffer(), 0, (int)body.Length),
            ["ip"] = http.Connection.RemoteIpAddress?.ToString(),
        };
        var result = await functions.InvokeAsync(name, request, FunctionCaller.Server, ct, kind: "http");
        if (!result.Ok) { await Send(ErrorOf(result)).ExecuteAsync(http); return; }
        var answer = result.Result as JsonObject ?? [];
        http.Response.StatusCode = answer["status"]?.GetValue<int>() ?? 200;
        foreach (var pair in answer["headers"] as JsonArray ?? [])
        {
            if (pair is not JsonArray { Count: 2 } kv || kv[0]?.GetValue<string>() is not { } key || Hop.Contains(key)) continue;
            http.Response.Headers.Append(key, kv[1]?.GetValue<string>());
        }
        if (answer["body"]?.GetValue<string>() is { Length: > 0 } b64 && http.Request.Method != "HEAD")
            await http.Response.Body.WriteAsync(Convert.FromBase64String(b64), ct);
    }
}
