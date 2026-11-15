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
    internal static WincheError ErrorOf(FunctionResult result)
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

        return app;
    }
}
