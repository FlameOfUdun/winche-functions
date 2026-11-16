using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Assets;

namespace Winche.Functions.Assets;

public static class AssetsFunctionsExtensions
{
    /// <summary>
    /// Gives functions <c>ctx.assets</c> (channels, releases and their files, with the server's rights: no rules), and
    /// lets them run on <c>assets.onReleased</c> / <c>assets.onChannelMoved</c>.
    /// </summary>
    public static IServiceCollection AddWincheFunctionsForAssets(this IServiceCollection services)
    {
        services.AddSingleton<IFunctionBinding, AssetsBinding>();
        return services;
    }
}

internal sealed class AssetsBinding : IFunctionBinding
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Name => "assets";

    /// <summary>The most a function reads in one call (the bytes cross to Deno as base64).</summary>
    public const long MaxBytes = 32L << 20;

    public async Task<JsonNode?> CallAsync(string op, JsonObject args, IServiceProvider services, CancellationToken ct)
    {
        var assets = services.GetService<WincheAssets>() ?? throw new FunctionCallException("unimplemented", "This app has no assets (AddWincheAssets).");
        string Arg(string name) => args[name]?.GetValue<string>() is { Length: > 0 } v ? v : throw new FunctionCallException("invalid-argument", $"Give {name}.");
        int Number(string name) => args[name]?.GetValueKind() == JsonValueKind.Number ? args[name]!.GetValue<int>() : throw new FunctionCallException("invalid-argument", $"Give {name}.");
        try
        {
            return op switch
            {
                "channels" => JsonSerializer.SerializeToNode((await assets.ChannelsAsync(ct)).Select(c => new { name = c.Name, release = c.Release, updatedAt = c.UpdatedAt, by = c.By }), Json),
                // The release a channel points at, with its files; null when there's no such channel.
                "channel" => (await assets.ChannelsAsync(ct)).FirstOrDefault(c => c.Name == Arg("name")) is { } c ? Release(await assets.ReleaseAsync(c.Release, ct)) : null,
                "release" => Release(await assets.ReleaseAsync(Number("number"), ct)),
                "releases" => JsonSerializer.SerializeToNode((await assets.ReleasesAsync(Math.Clamp(args["limit"]?.GetValue<int>() ?? 50, 1, 500), ct))
                    .Select(r => new { number = r.Number, note = r.Note, by = r.By, at = r.At, files = r.FileCount, size = r.TotalSize }), Json),
                "read" => await ReadAsync(assets, Number("release"), Arg("path"), ct),
                "downloadUrl" => await assets.DownloadUrlAsync(Number("release"), Arg("path"), ct) is { } link
                    ? new JsonObject { ["url"] = link.Url, ["expiresAt"] = link.ExpiresAt }
                    : throw new FunctionCallException("not-found", $"There's no {Arg("path")} in release {Number("release")}."),
                _ => throw new FunctionCallException("invalid-argument", $"ctx.assets.{op} isn't a thing."),
            };
        }
        catch (AssetsException ex) { throw new FunctionCallException(ex.Code, ex.Message); }
    }

    private static JsonNode? Release(AssetRelease? r) => r is null ? null : new JsonObject
    {
        ["number"] = r.Number, ["note"] = r.Note, ["by"] = r.By, ["at"] = r.At.ToString("O"),
        ["files"] = new JsonArray([.. r.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Key, ["hash"] = f.Value.Hash, ["size"] = f.Value.Size, ["mimeType"] = f.Value.MimeType,
        })]),
    };

    private static async Task<JsonNode?> ReadAsync(WincheAssets assets, int release, string path, CancellationToken ct)
    {
        if (await assets.OpenAsync(release, path, ct) is not { } opened) return null;
        await using var stream = opened.Content;
        if (opened.File.Size > MaxBytes)
            throw new FunctionCallException("out-of-range", $"{path} is {opened.File.Size} bytes: functions read up to {MaxBytes >> 20} MB at a time (use downloadUrl).");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return new JsonObject { ["base64"] = Convert.ToBase64String(copy.GetBuffer(), 0, (int)copy.Length), ["mimeType"] = opened.File.MimeType };
    }
}
