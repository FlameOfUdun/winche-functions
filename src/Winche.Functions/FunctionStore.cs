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
    ];

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_ready) return;
        await WincheSchema.EnsureAsync(db, "functions", Steps, ct);
        _ready = true;
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
        await using var cmd = db.CreateCommand("SELECT version, files, bundle, functions, note, deployed_by, deployed_at, rolled_back_from FROM winche_function_deployments WHERE version = $1");
        cmd.Parameters.AddWithValue(version);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r, withCode: true) : null;
    }

    /// <summary>Versions, newest first, without their code.</summary>
    public async Task<IReadOnlyList<Deployment>> ListAsync(int limit = 50, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT version, '{}'::jsonb, '', functions, note, deployed_by, deployed_at, rolled_back_from FROM winche_function_deployments ORDER BY version DESC LIMIT $1");
        cmd.Parameters.AddWithValue(limit);
        var list = new List<Deployment>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r, withCode: false));
        return list;
    }

    internal async Task<Deployment> AddAsync(IReadOnlyDictionary<string, string> files, string bundle, IReadOnlyList<FunctionInfo> functions, string? note, string? by, int? from, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("""
            INSERT INTO winche_function_deployments (version, files, bundle, functions, note, deployed_by, deployed_at, rolled_back_from)
            SELECT coalesce(max(version), 0) + 1, $1, $2, $3, $4, $5, now(), $6 FROM winche_function_deployments
            RETURNING version, files, bundle, functions, note, deployed_by, deployed_at, rolled_back_from
            """);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(files, Json));
        cmd.Parameters.AddWithValue(bundle);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(functions, Json));
        cmd.Parameters.AddWithValue((object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)by ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Read(r, withCode: true);
    }

    private static Deployment Read(NpgsqlDataReader r, bool withCode) => new(
        r.GetInt32(0),
        withCode ? JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1), Json)! : new Dictionary<string, string>(),
        r.GetString(2),
        JsonSerializer.Deserialize<List<FunctionInfo>>(r.GetString(3), Json)!,
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetInt32(7));
}
