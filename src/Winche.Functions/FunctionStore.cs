using Winche.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Winche.Functions;

/// <summary>Deployments in the project's database (table winche_function_deployments): every version is kept.</summary>
public sealed class FunctionStore([FromKeyedServices(FunctionStore.DataSourceKey)] NpgsqlDataSource db)
{
    public const string DataSourceKey = "winche-functions";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private bool _ready;

    internal NpgsqlDataSource Db => db;

    /// <summary>The deployments and schedules tables, in the order they were introduced (<see cref="WincheSchema"/>).</summary>
    internal static readonly string[] Steps =
    [
        """
            CREATE TABLE IF NOT EXISTS winche_function_deployments (
              version int PRIMARY KEY,
              files jsonb NOT NULL,
              bundle text NOT NULL,
              functions jsonb NOT NULL,
              note text NULL,
              deployed_by text NULL,
              deployed_at timestamptz NOT NULL,
              rolled_back_from int NULL
            );
            CREATE TABLE IF NOT EXISTS winche_function_schedules (
              name text PRIMARY KEY,
              last_due timestamptz NOT NULL
            );
        """,
        // Resources (ctx.resources): each content stored once by its hash; a deployment lists its paths and hashes.
        """
            CREATE TABLE IF NOT EXISTS winche_function_resources (
              hash text PRIMARY KEY,
              size bigint NOT NULL,
              content bytea NOT NULL,
              added_at timestamptz NOT NULL DEFAULT now()
            );
            ALTER TABLE winche_function_deployments ADD COLUMN IF NOT EXISTS resources jsonb NOT NULL DEFAULT '{}'::jsonb;
        """,
        // The servers running this project's functions: each notes it's alive (a row per running server).
        """
            CREATE UNLOGGED TABLE IF NOT EXISTS winche_function_servers (
              id text PRIMARY KEY,
              name text NOT NULL,
              seen_at timestamptz NOT NULL
            );
        """,
        // Plugins installed in each deployment.
        """
            ALTER TABLE winche_function_deployments ADD COLUMN IF NOT EXISTS plugins jsonb NOT NULL DEFAULT '[]'::jsonb;
        """,
    ];

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_ready) return;
        await WincheSchema.EnsureAsync(db, "functions", Steps, ct);
        _ready = true;
    }

    /// <summary>Notes that server <paramref name="id"/> (shown as <paramref name="name"/>) runs now; forgets servers gone for a day.</summary>
    internal async Task SeenAsync(string id, string name, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var batch = db.CreateBatch();
        var seen = new NpgsqlBatchCommand("""
            INSERT INTO winche_function_servers (id, name, seen_at) VALUES ($1, $2, now())
              ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, seen_at = now()
            """);
        seen.Parameters.AddWithValue(id);
        seen.Parameters.AddWithValue(name);
        batch.BatchCommands.Add(seen);
        batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM winche_function_servers WHERE seen_at < now() - interval '1 day'"));
        await batch.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The servers seen within <paramref name="within"/>, but <paramref name="except"/>: their names.</summary>
    internal async Task<IReadOnlyList<string>> ServersSeenAsync(TimeSpan within, string except, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT name FROM winche_function_servers WHERE seen_at > now() - $1 AND id <> $2 ORDER BY name");
        cmd.Parameters.AddWithValue(within);
        cmd.Parameters.AddWithValue(except);
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        return names;
    }

    /// <summary>A stopping server's row goes at once.</summary>
    internal async Task GoneAsync(string id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("DELETE FROM winche_function_servers WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> LatestVersionAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT coalesce(max(version), 0) FROM winche_function_deployments");
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<Deployment?> GetAsync(int version, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT version, files, bundle, functions, note, deployed_by, deployed_at, rolled_back_from, resources, plugins FROM winche_function_deployments WHERE version = $1");
        cmd.Parameters.AddWithValue(version);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r, withCode: true) : null;
    }

    /// <summary>Versions, newest first, without their code.</summary>
    public async Task<IReadOnlyList<Deployment>> ListAsync(int limit = 50, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT version, '{}'::jsonb, '', functions, note, deployed_by, deployed_at, rolled_back_from, resources, plugins FROM winche_function_deployments ORDER BY version DESC LIMIT $1");
        cmd.Parameters.AddWithValue(limit);
        var list = new List<Deployment>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r, withCode: false));
        return list;
    }

    internal async Task<Deployment> AddAsync(IReadOnlyDictionary<string, string> files, string bundle, IReadOnlyList<FunctionInfo> functions, string? note, string? by, int? from,
        IReadOnlyDictionary<string, ResourceEntry> resources, IReadOnlyList<PluginInstall> plugins, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("""
            INSERT INTO winche_function_deployments (version, files, bundle, functions, note, deployed_by, deployed_at, rolled_back_from, resources, plugins)
            SELECT coalesce(max(version), 0) + 1, $1, $2, $3, $4, $5, now(), $6, $7, $8 FROM winche_function_deployments
            RETURNING version, files, bundle, functions, note, deployed_by, deployed_at, rolled_back_from, resources, plugins
            """);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(files, Json));
        cmd.Parameters.AddWithValue(bundle);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(functions, Json));
        cmd.Parameters.AddWithValue((object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)by ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(resources, Json));
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(plugins, Json));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Read(r, withCode: true);
    }

    private static Deployment Read(NpgsqlDataReader r, bool withCode) => new(
        r.GetInt32(0),
        withCode ? JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1), Json)! : new Dictionary<string, string>(),
        r.GetString(2),
        JsonSerializer.Deserialize<List<FunctionInfo>>(r.GetString(3), Json)!,
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetInt32(7),
        JsonSerializer.Deserialize<Dictionary<string, ResourceEntry>>(r.GetString(8), Json)!,
        JsonSerializer.Deserialize<List<PluginInstall>>(r.GetString(9), Json)!);

    // ── resources ────────────────────────────────────────────────────────────────

    /// <summary>Which of <paramref name="hashes"/> aren't stored yet.</summary>
    public async Task<IReadOnlyList<string>> MissingResourcesAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (hashes.Count == 0) return [];
        await using var cmd = db.CreateCommand("SELECT hash FROM winche_function_resources WHERE hash = ANY($1)");
        cmd.Parameters.AddWithValue(hashes.Distinct().ToArray());
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) found.Add(r.GetString(0));
        return [.. hashes.Distinct().Where(h => !found.Contains(h))];
    }

    /// <summary>Stores an resource's content under its hash (once; storing it again changes nothing).</summary>
    internal async Task PutResourceAsync(string hash, byte[] content, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("INSERT INTO winche_function_resources (hash, size, content) VALUES ($1, $2, $3) ON CONFLICT (hash) DO NOTHING");
        cmd.Parameters.AddWithValue(hash);
        cmd.Parameters.AddWithValue((long)content.Length);
        cmd.Parameters.AddWithValue(content);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>An resource's content, or null.</summary>
    internal async Task<byte[]?> ResourceAsync(string hash, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT content FROM winche_function_resources WHERE hash = $1");
        cmd.Parameters.AddWithValue(hash);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }
}
