using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Storage.Services;

namespace Winche.Functions.Storage;

public static class StorageFunctionsExtensions
{
    /// <summary>
    /// Gives functions <c>ctx.files</c> (list, get, read, write, download links and delete, with the server's rights), and runs <c>files.beforeWrite</c>
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

    /// <summary>The most a function reads or writes in one call (the bytes cross to Deno as base64).</summary>
    public const long MaxBytes = 32L << 20;

    public async Task<JsonNode?> CallAsync(string op, JsonObject args, IServiceProvider services, CancellationToken ct)
    {
        var files = services.GetRequiredService<FileStorage>();
        string Arg(string name) => args[name]?.GetValue<string>() is { Length: > 0 } v ? v : throw new FunctionCallException("invalid-argument", $"Give {name}.");
        string? Text(string name) => args[name]?.GetValueKind() == JsonValueKind.String ? args[name]!.GetValue<string>() : null;
        try
        {
            return op switch
            {
                "list" => JsonSerializer.SerializeToNode(await ListAllAsync(files, Arg("folder").Trim('/'), ct), Json),
                // One page of a folder: files and subfolders in name order, and the token for the next page.
                "page" => JsonSerializer.SerializeToNode(await files.ListAsync((Text("folder") ?? "").Trim('/'),
                    new Winche.Storage.Models.FileListOptions(MaxResults: args["maxResults"]?.GetValue<int>(), PageToken: Text("pageToken"), MimeType: Text("mimeType")), ct), Json),
                // Merges metadata (null removes a key), optionally changes the download headers ("" clears one), only at ifVersion if given.
                "updateMetadata" => await files.UpdateMetadataAsync(Arg("path").TrimStart('/'), args["metadata"] as JsonObject ?? [],
                    args["ifVersion"]?.GetValue<long>() is { } v ? new Winche.Storage.Models.FilePrecondition(Version: v) : null,
                    new Winche.Storage.Models.FileHeaders(Text("contentDisposition"), Text("cacheControl")), ct) is { } updated
                    ? JsonSerializer.SerializeToNode(updated, Json) : throw new FunctionCallException("not-found", $"There's no file {Arg("path")}."),
                "get" => await files.GetAsync(Arg("path").TrimStart('/'), ct) is { } record ? JsonSerializer.SerializeToNode(record, Json) : null,
                "read" => await ReadAsync(files, Arg("path").TrimStart('/'), ct),
                "write" => await WriteAsync(files, Arg("path").TrimStart('/'), args["base64"]?.GetValue<string>() ?? "", Text("mimeType"),
                    args["metadata"] as JsonObject, new Winche.Storage.Models.FileHeaders(Text("contentDisposition"), Text("cacheControl")), ct),
                "copyFromUrl" => await CopyFromUrlAsync(files, Arg("path").TrimStart('/'), Arg("url"), args["headers"] as JsonObject, Text("mimeType"),
                    args["metadata"] as JsonObject, new Winche.Storage.Models.FileHeaders(Text("contentDisposition"), Text("cacheControl")), ct),
                "downloadUrl" => await files.GenerateDownloadUrlAsync(Arg("path").TrimStart('/'), ct) is var link
                    ? new JsonObject { ["url"] = link.Url, ["expiresAt"] = link.ExpiresAt } : null,
                "delete" => await files.DeleteAsync(Arg("path").TrimStart('/'), ct: ct),
                "deleteUnder" => await files.DeleteUnderAsync(Arg("folder").TrimStart('/') is var f && f.EndsWith('/') ? f : f + "/", ct),
                _ => throw new FunctionCallException("invalid-argument", $"ctx.files.{op} isn't a thing."),
            };
        }
        catch (Winche.Storage.Models.FileRecordNotFoundException) { throw new FunctionCallException("not-found", $"There's no file {Arg("path")}."); }
        catch (Winche.Storage.Models.InvalidUploadStatusException) { throw new FunctionCallException("failed-precondition", $"{Arg("path")} hasn't finished uploading."); }
        catch (Winche.Storage.Models.StorageException ex) { throw new FunctionCallException(ex.Error.Code, ex.Message); }
        catch (NotSupportedException ex) { throw new FunctionCallException("unimplemented", ex.Message); }
        catch (ArgumentException ex) { throw new FunctionCallException("invalid-argument", ex.Message); }
    }

    private static async Task<JsonNode?> ReadAsync(FileStorage files, string path, CancellationToken ct)
    {
        var record = await files.GetAsync(path, ct);
        if (record is null) return null;
        if (record.SizeBytes > MaxBytes) throw new FunctionCallException("out-of-range", $"{path} is {record.SizeBytes} bytes: functions read up to {MaxBytes >> 20} MB at a time.");
        await using var stream = await files.OpenReadAsync(path, ct);
        if (stream is null) throw new FunctionCallException("failed-precondition", $"{path} hasn't finished uploading.");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return new JsonObject { ["base64"] = Convert.ToBase64String(copy.GetBuffer(), 0, (int)copy.Length), ["mimeType"] = record.MimeType };
    }

    private static async Task<JsonNode?> WriteAsync(FileStorage files, string path, string base64, string? mimeType, JsonObject? metadata,
        Winche.Storage.Models.FileHeaders headers, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new FunctionCallException("invalid-argument", "The file's content didn't arrive whole."); }
        if (bytes.Length > MaxBytes) throw new FunctionCallException("out-of-range", $"Functions write up to {MaxBytes >> 20} MB at a time.");
        var record = await files.WriteAsync(path, new MemoryStream(bytes), string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType,
            bytes.Length, metadata?.DeepClone() as JsonObject, headers, ct);
        return JsonSerializer.SerializeToNode(record, Json);
    }

    /// <summary>The most one copyFromUrl takes (it passes through the server's disk, not the function).</summary>
    public const long MaxCopyBytes = 5L << 30;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Writes a file from a URL (GET, with the given headers), any size up to <see cref="MaxCopyBytes"/>: the bytes go
    /// from the URL to storage through a temporary file on the server, never through the function.
    /// </summary>
    private static async Task<JsonNode?> CopyFromUrlAsync(FileStorage files, string path, string url, JsonObject? headers, string? mimeType,
        JsonObject? metadata, Winche.Storage.Models.FileHeaders fileHeaders, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new FunctionCallException("invalid-argument", "url is an http(s) address.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, value) in headers ?? [])
            if (value?.GetValueKind() == JsonValueKind.String) request.Headers.TryAddWithoutValidation(name, value.GetValue<string>());
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new FunctionCallException((int)response.StatusCode == 404 ? "not-found" : "unavailable",
                $"{uri.Host} answered {(int)response.StatusCode} for {path}.");
        if (response.Content.Headers.ContentLength > MaxCopyBytes)
            throw new FunctionCallException("out-of-range", $"{path} is larger than {MaxCopyBytes >> 30} GB.");
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"winche-copy-{Guid.NewGuid():N}");
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxCopyBytes) throw new FunctionCallException("out-of-range", $"{path} is larger than {MaxCopyBytes >> 30} GB.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            var type = !string.IsNullOrWhiteSpace(mimeType) ? mimeType : response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            await using var content = File.OpenRead(temp);
            var record = await files.WriteAsync(path, content, type, content.Length, metadata?.DeepClone() as JsonObject, fileHeaders, ct);
            return JsonSerializer.SerializeToNode(record, Json);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
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
