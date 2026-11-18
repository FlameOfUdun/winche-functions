using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Runtime.Expansion;
using Winche.Values;

namespace Winche.Functions.Database;

public static class DatabaseFunctionsExtensions
{
    /// <summary>
    /// Gives functions <c>ctx.db</c> (documents read and written with the server's rights; access rules don't apply), and
    /// runs <c>documents.beforeWrite</c> functions before matching writes are saved.
    /// </summary>
    public static IServiceCollection AddWincheFunctionsForDatabase(this IServiceCollection services)
    {
        services.AddSingleton<IFunctionBinding, DatabaseBinding>();
        // One hook for every document: it asks Deno only when a deployed before-function matches the path.
        services.AddSingleton(sp => new Winche.Database.Abstraction.BeforeHookRegistration("{path=**}",
            new FunctionsBeforeWrite(sp.GetRequiredService<BeforeFunctions>())));
        return services;
    }
}

/// <summary>Documents in the database's wire format (typed values), as the WebSocket protocol sends them.</summary>
internal static class Wire
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonNode? Of<T>(T? value) => value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType());

    /// <summary>A document's fields as tagged values (for changes and before-functions, where only the fields are known).</summary>
    public static JsonObject Fields(IReadOnlyDictionary<string, Value> fields) => ValueSerializer.WriteFields(fields);
}

/// <summary>documents.beforeWrite: the deployed functions refuse or change a document write.</summary>
internal sealed class FunctionsBeforeWrite(BeforeFunctions functions) : Winche.Database.Abstraction.DocumentBeforeHook
{
    public override Task OnBeforeWriteAsync(Winche.Database.Abstraction.DocumentWrite write, CancellationToken ct)
    {
        if (!functions.Any("documents")) return Task.CompletedTask;
        var caller = BeforeFunctions.Caller(write.Caller);
        return functions.RunAsync("documents", "write", write.Path, captures => new JsonObject
        {
            ["operation"] = write.Operation.ToString().ToLowerInvariant(), ["path"] = write.Path, ["params"] = captures,
            ["before"] = Wire.Of(write.Before),
            ["data"] = write.Fields is { } fields ? new JsonObject { ["fields"] = Wire.Fields(fields) } : null,
            ["auth"] = BeforeFunctions.Auth(caller),
        }, caller, result =>
        {
            if (result is not JsonObject changes) return;
            if (changes["set"] is JsonObject set)
                foreach (var (field, value) in set) if (value is not null) write.Set(field, ValueSerializer.Read(value));
            if (changes["remove"] is JsonArray remove)
                foreach (var field in remove) if (field?.GetValue<string>() is { Length: > 0 } f) write.Remove(f);
        }, ct);
    }
}

/// <summary>
/// @winche-technologies/winche-admin's documents: reads, queries, counts, aggregations, writes and adds in the database's wire format (the
/// same messages the WebSocket protocol takes), with the server's rights, or as an app user under the access rules.
/// </summary>
internal sealed class DatabaseBinding : IFunctionBinding, IActsAsAppUser
{
    public string Name => "db";

    public async Task<JsonNode?> CallAsync(string op, JsonObject args, IServiceProvider services, CancellationToken ct)
    {
        // As one of the app's users (as(user)): the rule-guarded database, with them as the caller; else the server's rights.
        var asUser = services.GetService<AdminCaller>()?.As;
        using var caller = asUser is null ? null : WincheCaller.Use(AdminOps.Claims(asUser));
        IDocumentDatabase db = asUser is null ? services.GetRequiredService<DocumentDatabase>() : services.GetRequiredService<IDocumentDatabase>();
        try
        {
            switch (op)
            {
                case "get":
                {
                    var doc = await db.GetAsync(Path(args), ct);
                    return await WithIncluded(new JsonObject { ["document"] = Wire.Of(doc) }, [doc], args, services, db, ct);
                }
                case "getAll":
                {
                    var paths = (args["paths"] as JsonArray ?? throw new FunctionCallException("invalid-argument", "Give paths."))
                        .Select(p => p?.GetValue<string>()?.Trim('/') ?? "").ToList();
                    var docs = await db.GetAllAsync(paths, ct);
                    return await WithIncluded(new JsonObject { ["documents"] = new JsonArray([.. docs.Select(Wire.Of)]) }, docs, args, services, db, ct);
                }
                case "query":
                {
                    var result = await db.QueryAsync(Query(args), ct);
                    return await WithIncluded(Wire.Of(result)!.AsObject(), result.Documents, args, services, db, ct);
                }
                case "count":
                    return new JsonObject { ["count"] = await db.CountAsync(Query(args) with { Limit = null, Offset = null }, ct) };
                case "aggregate":
                {
                    var aggregations = (args["aggregations"] as JsonArray ?? throw new FunctionCallException("invalid-argument", "Give aggregations."))
                        .Select(Aggregation).ToList();
                    var result = await db.AggregateAsync(Query(args) with { Limit = null }, aggregations, ct);
                    var values = new JsonObject();
                    foreach (var (alias, value) in result.Values) values[alias] = ValueSerializer.Write(value);
                    return new JsonObject { ["result"] = values };
                }
                case "write":
                {
                    var items = args["writes"] as JsonArray ?? throw new FunctionCallException("invalid-argument", "Give writes.");
                    if (items.Count is 0 or > 500) throw new FunctionCallException("invalid-argument", "A batch has 1 to 500 writes.");
                    var writes = items.Select(w => w.Deserialize<Write>() ?? throw new FunctionCallException("invalid-argument", "A write is null.")).ToList();
                    var results = await db.WriteAsync(writes, ct);
                    return new JsonObject { ["writeResults"] = new JsonArray([.. results.Select(r => Wire.Of(r))]) };
                }
                case "add":
                {
                    var collection = args["collection"]?.GetValue<string>()?.Trim('/') is { Length: > 0 } c ? c : throw new FunctionCallException("invalid-argument", "Give a collection.");
                    var fields = args["fields"] as JsonObject ?? throw new FunctionCallException("invalid-argument", "Give the fields.");
                    return new JsonObject { ["document"] = Wire.Of(await db.AddAsync(collection, ValueSerializer.ReadFields(fields), ct)) };
                }
                case "deleteTree":
                    // Everything under a path, in batches (no single unbounded transaction): the server's only.
                    if (db is not DocumentDatabase core) throw new FunctionCallException("invalid-argument", "recursiveDelete isn't available acting as a user: delete the documents one by one.");
                    return await core.DeleteSubtreeAsync(Path(args), ct: ct);
                default:
                    throw new FunctionCallException("invalid-argument", $"ctx.documents: '{op}' isn't an operation.");
            }
        }
        catch (Winche.Database.Authorization.AccessDeniedException ex) { throw new FunctionCallException("permission-denied", ex.Message); }
        catch (QueryParseException ex) { throw new FunctionCallException("invalid-argument", $"{ex.Message} ({ex.JsonPath})."); }
        catch (JsonException ex) { throw new FunctionCallException("invalid-argument", ex.Message); }
        catch (WireFormatException ex) { throw new FunctionCallException("invalid-argument", ex.Message); }
        catch (RuntimeException ex)
        {
            throw new FunctionCallException(ex.Status switch
            {
                RuntimeStatus.NotFound => "not-found", RuntimeStatus.AlreadyExists => "already-exists", RuntimeStatus.FailedPrecondition => "failed-precondition",
                RuntimeStatus.Aborted => "aborted", RuntimeStatus.DeadlineExceeded => "deadline-exceeded", _ => "invalid-argument",
            }, ex.Message);
        }
    }

    private static Query Query(JsonObject args) =>
        QueryParser.Parse(args["query"] as JsonObject ?? throw new FunctionCallException("invalid-argument", "Give a query."));

    private static Aggregation Aggregation(JsonNode? node)
    {
        var o = node as JsonObject ?? throw new FunctionCallException("invalid-argument", "Each aggregation is { kind, alias, field? }.");
        var alias = o["alias"]?.GetValue<string>() is { Length: > 0 } a ? a : throw new FunctionCallException("invalid-argument", "Each aggregation has an alias.");
        string Field() => o["field"]?.GetValue<string>() is { Length: > 0 } f ? f : throw new FunctionCallException("invalid-argument", $"{alias}: give the field.");
        return o["kind"]?.GetValue<string>() switch
        {
            "count" => Winche.Database.Querying.Aggregation.Count(alias),
            "sum" => Winche.Database.Querying.Aggregation.Sum(Field(), alias),
            "average" => Winche.Database.Querying.Aggregation.Average(Field(), alias),
            var k => throw new FunctionCallException("invalid-argument", $"'{k}' isn't an aggregation (count, sum, average)."),
        };
    }

    /// <summary>Adds included (the referenced documents, by path) and resolved (files, users) when expand was asked for.</summary>
    private static async Task<JsonObject> WithIncluded(JsonObject body, IEnumerable<Document?> docs, JsonObject args, IServiceProvider services,
        IDocumentDatabase db, CancellationToken ct)
    {
        if (Expand(args, services) is not { } expand) return body;
        var expansion = await ReferenceExpander.ExpandAsync(db, docs, expand, ct, services.GetService<ReferenceResolvers>()?.For(db));
        var included = new JsonObject();
        foreach (var (path, doc) in expansion.Included) included[path] = Wire.Of(doc);
        body["included"] = included;
        var resolved = new JsonObject();
        foreach (var (key, value) in expansion.Resolved) resolved[key] = value.DeepClone();
        body["resolved"] = resolved;
        return body;
    }

    /// <summary>The <c>expand</c> option: reference fields to follow (server rights: every referenced document that exists).</summary>
    private static ExpandRequest? Expand(JsonObject args, IServiceProvider services) => args["expand"] switch
    {
        null => null,
        JsonArray a => ExpandRequest.Of(a.Select(n => n?.GetValue<string>() ?? ""),
            services.GetService<Microsoft.Extensions.Options.IOptions<Winche.Database.DependencyInjection.WincheDatabaseOptions>>()?.Value.ExpandLimit ?? ExpandRequest.DefaultLimit),
        _ => throw new FunctionCallException("invalid-argument", "expand is a list of field paths, e.g. [\"patient\", \"patient.doctor\"]."),
    };

    private static string Path(JsonObject args) =>
        args["path"]?.GetValue<string>()?.Trim('/') is { Length: > 0 } p ? p : throw new FunctionCallException("invalid-argument", "Give a document path, e.g. \"patients/p1\".");
}
