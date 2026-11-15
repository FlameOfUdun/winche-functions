using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.Services;

namespace Winche.Functions.Storage;

public static class StorageFunctionsExtensions
{
    /// <summary>
    /// Gives functions <c>ctx.files</c> (list and delete files with the server's rights), and runs <c>files.beforeWrite</c>
    /// functions before matching file writes are saved.
    /// </summary>
    public static IServiceCollection AddWincheFunctionsForStorage(this IServiceCollection services)
    {
        services.AddSingleton<IFunctionBinding, StorageBinding>();
        services.AddSingleton(sp => new Winche.Storage.Abstraction.FileBeforeHookRegistration("{path=**}",
            new FunctionsBeforeFileWrite(sp.GetRequiredService<BeforeFunctions>())));
        return services;
    }
}

/// <summary>files.beforeWrite: the deployed functions refuse a file write or change its metadata and headers.</summary>
internal sealed class FunctionsBeforeFileWrite(BeforeFunctions functions) : Winche.Storage.Abstraction.FileBeforeHook
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override Task OnBeforeWriteAsync(Winche.Storage.Abstraction.FileWrite write, CancellationToken ct)
    {
        if (!functions.Any("files")) return Task.CompletedTask;
        var caller = BeforeFunctions.Caller(write.Caller);
        return functions.RunAsync("files", "write", write.Path, captures => new JsonObject
        {
            ["operation"] = write.Operation.ToString().ToLowerInvariant(), ["path"] = write.Path, ["params"] = captures,
            ["before"] = write.Before is null ? null : JsonSerializer.SerializeToNode(write.Before, Json),
            ["file"] = write.Metadata is null ? null : new JsonObject
            {
                ["mimeType"] = write.MimeType, ["sizeBytes"] = write.SizeBytes, ["metadata"] = write.Metadata.DeepClone(),
                ["contentDisposition"] = write.ContentDisposition, ["cacheControl"] = write.CacheControl,
            },
            ["auth"] = BeforeFunctions.Auth(caller),
        }, caller, result =>
        {
            if (result is not JsonObject changes || write.Metadata is null) return;   // a delete can be refused, not changed
            if (changes["metadata"] is JsonObject metadata)
                foreach (var (key, value) in metadata)
                    if (value is null) write.Metadata.Remove(key);
                    else write.Metadata[key] = value.DeepClone();
            if (changes.ContainsKey("contentDisposition")) write.ContentDisposition = changes["contentDisposition"]?.GetValue<string>();
            if (changes.ContainsKey("cacheControl")) write.CacheControl = changes["cacheControl"]?.GetValue<string>();
        }, ct);
    }
}

internal sealed class StorageBinding : IFunctionBinding
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Name => "files";

    public async Task<JsonNode?> CallAsync(string op, JsonObject args, IServiceProvider services, CancellationToken ct)
    {
        var files = services.GetRequiredService<FileStorage>();
        string Arg(string name) => args[name]?.GetValue<string>() is { Length: > 0 } v ? v : throw new FunctionCallException("invalid-argument", $"Give {name}.");
        return op switch
        {
            "list" => JsonSerializer.SerializeToNode(await ListAllAsync(files, Arg("folder").Trim('/'), ct), Json),
            "delete" => await files.DeleteAsync(Arg("path").TrimStart('/'), ct: ct),
            "deleteUnder" => await files.DeleteUnderAsync(Arg("folder").TrimStart('/') is var f && f.EndsWith('/') ? f : f + "/", ct),
            _ => throw new FunctionCallException("invalid-argument", $"ctx.files.{op} isn't a thing."),
        };
    }

    /// <summary>Every file directly in <paramref name="folder"/>, across pages.</summary>
    private static async Task<List<Winche.Storage.Models.FileRecord>> ListAllAsync(FileStorage files, string folder, CancellationToken ct)
    {
        var all = new List<Winche.Storage.Models.FileRecord>();
        string? token = null;
        do
        {
            var page = await files.ListAsync(folder, new(PageToken: token), ct);
            all.AddRange(page.Items);
            token = page.NextPageToken;
        } while (token is not null);
        return all;
    }
}
