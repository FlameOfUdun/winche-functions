using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Realtime;

namespace Winche.Functions.Realtime;

public static class RealtimeFunctionsExtensions
{
    /// <summary>
    /// Gives functions <c>realtime</c> in @winche-technologies/winche-admin (lists, channels and presence, with the
    /// server's rights: no rules), and lets them run on <c>onRealtime.closed</c>.
    /// </summary>
    public static IServiceCollection AddWincheFunctionsForRealtime(this IServiceCollection services)
    {
        services.AddSingleton<IFunctionBinding, RealtimeBinding>();
        return services;
    }
}

/// <summary><c>realtime.*</c>: what a function (or a server with the admin SDK) does with Realtime, as the server.</summary>
internal sealed class RealtimeBinding : IFunctionBinding
{
    public string Name => "realtime";

    public async Task<JsonNode?> CallAsync(string op, JsonObject args, IServiceProvider services, CancellationToken ct)
    {
        var lists = services.GetService<RealtimeLists>() ?? throw new FunctionCallException("unimplemented", "This app has no realtime (AddWincheRealtime).");
        string Arg(string name) => args[name]?.GetValueKind() == JsonValueKind.String && args[name]!.GetValue<string>() is { Length: > 0 } v ? v
            : throw new FunctionCallException("invalid-argument", $"Give {name}.");
        byte[] Bytes(JsonNode? b64)
        {
            try { return Convert.FromBase64String(b64?.GetValueKind() == JsonValueKind.String ? b64.GetValue<string>() : ""); }
            catch (FormatException) { throw new FunctionCallException("invalid-argument", "A payload is base64."); }
        }
        try
        {
            switch (op)
            {
                case "append":
                    var items = (args["items"] as JsonArray ?? throw new FunctionCallException("invalid-argument", "Give items: [{ key, payload }]."))
                        .Select(i => new RealtimeItemIn(i?["key"]?.GetValueKind() == JsonValueKind.String ? i["key"]!.GetValue<string>() : "", Bytes(i?["payload"]))).ToList();
                    var positions = await lists.ServerAppendAsync(Arg("path"), items, ct);
                    return new JsonObject { ["positions"] = new JsonArray([.. positions.Select(p => (JsonNode)p)]) };
                case "read":
                    var page = await lists.Store.ReadAsync(Arg("path"), args["after"]?.GetValue<long>() ?? 0, args["limit"]?.GetValue<int>() ?? 500, ct);
                    return new JsonObject
                    {
                        ["items"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
                        {
                            ["key"] = i.Key, ["position"] = i.Position, ["at"] = i.At.ToString("O"), ["by"] = i.By, ["payload"] = Convert.ToBase64String(i.Payload),
                        })]),
                        ["list"] = page.List is { } l ? List(l) : null,
                    };
                case "open":
                    return new JsonObject { ["list"] = List(await lists.ServerOpenAsync(Arg("path"), ct)) };
                case "close":
                    return new JsonObject { ["list"] = List(await lists.ServerCloseAsync(Arg("path"), ct)) };
                case "publish":
                    await services.GetRequiredService<RealtimeChannels>().ServerPublishAsync(Arg("path"), Arg("channel"), Bytes(args["payload"]), ct);
                    return new JsonObject();
                case "members":
                    return new JsonArray([.. (await services.GetRequiredService<RealtimePresence>().MembersAsync(Arg("path"), ct)).Select(m => (JsonNode)new JsonObject
                    {
                        ["uid"] = m.Uid, ["data"] = m.Data?.DeepClone(), ["joinedAt"] = m.JoinedAt.ToString("O"),
                    })]);
                default:
                    throw new FunctionCallException("invalid-argument", $"realtime.{op} isn't a thing: append, read, open, close, publish or members.");
            }
        }
        catch (RealtimeException ex) { throw new FunctionCallException(WincheError.Name(ex.Code), ex.Message); }
    }

    private static JsonObject List(RealtimeListInfo l) => new()
    {
        ["path"] = l.Path, ["state"] = l.State, ["count"] = l.Count, ["bytes"] = l.Bytes,
        ["openedAt"] = l.OpenedAt.ToString("O"), ["closedAt"] = l.ClosedAt?.ToString("O"),
    };
}
