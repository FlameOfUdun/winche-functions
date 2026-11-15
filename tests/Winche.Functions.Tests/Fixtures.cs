using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Winche.Auth.DependencyInjection;
using Winche.Core;
using Winche.Database.DependencyInjection;
using Winche.Functions.Auth;
using Winche.Functions.Database;
using Winche.Functions.Storage;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
using Winche.Webhooks;

namespace Winche.Functions.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> NewDatabaseAsync()
    {
        var name = $"f{Guid.NewGuid():N}"[..20];
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>Deno for tests: WINCHE_DENO, else deno on PATH.</summary>
public static class Deno
{
    public static string? Path => Environment.GetEnvironmentVariable("WINCHE_DENO") is { Length: > 0 } p ? p : null;
}

internal sealed class MemoryArchive : IArchive
{
    public ConcurrentDictionary<string, long> Objects { get; } = new();   // path → size
    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) => Task.FromResult(new UploadSession { Url = $"mem://{path}" });
    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, DownloadHeaders? headers = null, CancellationToken ct = default) => Task.FromResult(new DownloadSession { Url = $"mem://{path}", ExpiresAt = default });
    public Task<ArchiveObjectInfo?> HeadAsync(string path, CancellationToken ct = default) => Task.FromResult(Objects.TryGetValue(path, out var o) ? new ArchiveObjectInfo(o, "\"e\"") : (ArchiveObjectInfo?)null);
    public Task DeleteAsync(string path, CancellationToken ct = default) { Objects.TryRemove(path, out _); return Task.CompletedTask; }
    public Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default) { foreach (var p in paths) Objects.TryRemove(p, out _); return Task.CompletedTask; }
    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
    public Task<string> CreateMultipartUploadAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) => Task.FromResult("u");
    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default) => Task.FromResult(new UploadSession { Url = "mem://p" });
    public Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => Task.FromResult<string?>("e");
    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) => Task.FromResult<IEnumerable<FilePart>>([]);
    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>An app with the whole stack and functions, on a real port; callers identify with the X-Test-Uid header.</summary>
public sealed class FunctionsApp : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required string Url { get; init; }
    public HttpClient Http { get; } = new();
    public T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();
    internal MemoryArchive Archive { get; init; } = new();

    public static async Task<FunctionsApp> StartAsync(string db)
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        b.Services.AddWincheConfig(o => { o.ConnectionString = db; o.MasterKey = "functions-tests-master-key-0123456789abcdef"; });
        b.Services.AddWincheHooks(o => { o.ConnectionString = db; o.PollInterval = TimeSpan.FromMilliseconds(50); o.RetryDelays = [TimeSpan.FromMilliseconds(200)]; });
        b.Services.AddWincheDatabase(o =>
        {
            o.ConnectionString = db;
            o.UseRulesText("match {document=**} { allow read, write: if true; }");
            o.ChangeFeed = o.ChangeFeed with { PollInterval = TimeSpan.FromMilliseconds(100) };
            o.UseHooks(h => h.PublishChanges());
        });
        var archive = new MemoryArchive();
        b.Services.AddWincheStorage(o =>
        {
            o.ConnectionString = db;
            o.HookDelivery = new HookDeliveryOptions { PollInterval = TimeSpan.FromMilliseconds(100) };
            o.UseHooks(h => h.PublishChanges());
        });
        b.Services.AddSingleton<IArchive>(archive);
        b.Services.AddWincheAuth(o => { o.ConnectionString = db; o.Issuer = "https://test.example"; o.EmailPassword(); });
        b.Services.AddWincheFunctions(o =>
        {
            o.ConnectionString = db;
            o.DenoPath = Deno.Path;
            o.SyncInterval = TimeSpan.FromMilliseconds(300);
        });
        // One caller for every package (Winche.Core), as Winche Server sets it.
        b.Services.AddWincheCaller(http => http.Request.Headers["X-Test-Uid"].ToString() is { Length: > 0 } uid
            ? new Dictionary<string, object?> { ["uid"] = uid, ["role"] = "clinician" } : null);
        b.Services.AddWincheFunctionsForDatabase().AddWincheFunctionsForStorage().AddWincheFunctionsForAuth();
        var app = b.Build();
        await app.InitializeWincheDatabaseAsync();
        await app.InitializeWincheStorageAsync();
        app.UseWincheCaller();
        app.MapWincheFunctions();
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return new FunctionsApp { App = app, Url = url, Archive = archive };
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
