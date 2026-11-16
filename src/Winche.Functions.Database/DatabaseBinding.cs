using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Runtime.Expansion;
using Winche.Values;
using Winche.Database.Documents;

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

/// <summary>
/// A document as functions see it, with the references <c>expand</c> reached replaced by the documents they point to
/// (each in the same shape, its own fields expanded along the rest of the path). One that doesn't exist stays a path.
/// </summary>
internal static class Expanded
{
    public static JsonObject? Of(Document? doc, ExpandRequest expand, Expansion expansion) => Of(doc, [.. expand.Paths], expansion);

    private static JsonObject? Of(Document? doc, List<ExpandPath> paths, Expansion expansion)
    {
        var plain = Plain.Of(doc);
        if (plain is null || doc is null) return plain;
        plain["fields"] = Fields(doc.Fields, paths, expansion);
        return plain;
    }

    private static JsonObject Fields(IReadOnlyDictionary<string, Value> fields, List<ExpandPath> paths, Expansion expansion)
    {
        var o = new JsonObject();
        foreach (var (key, value) in fields)
            o[key] = Node(value, [.. paths.Where(p => p.Segments.Length > 0 && p.Segments[0] == key).Select(p => new ExpandPath(p.Segments[1..]))], expansion);
        return o;
    }

    private static JsonNode? Node(Value value, List<ExpandPath> paths, Expansion expansion)
    {
        if (paths.Count == 0) return PlainJson.Of(value);
        var ends = paths.Any(p => p.Segments.Length == 0);
        return value switch
        {
            // A file or user reference a path ends at: the file's record, the user's account.
            FileReferenceValue f when ends && expansion.Resolved.TryGetValue($"file:{f.Path}", out var file) => file.DeepClone(),
            UserReferenceValue u when ends && expansion.Resolved.TryGetValue($"user:{u.Uid}", out var user) => user.DeepClone(),
            DocumentReferenceValue r when expansion.Included.TryGetValue(r.Path.Trim('/'), out var doc) =>
                Of(doc, [.. paths.Where(p => p.Segments.Length > 0)], expansion),
            ArrayValue a => new JsonArray([.. a.Values.Select(v => Node(v, paths, expansion))]),
            MapValue m => Fields(m.Fields, paths, expansion),
            _ => PlainJson.Of(value),
        };
    }
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
            ["before"] = Plain.Of(write.Before),
            ["data"] = write.Fields is { } fields ? new JsonObject { ["fields"] = PlainJson.Fields(fields) } : null,
            ["auth"] = BeforeFunctions.Auth(caller),
        }, caller, result =>
        {
            if (result is not JsonObject changes) return;
            if (changes["set"] is JsonObject set)
                foreach (var (field, value) in set) write.Set(field, DatabaseBinding.ToValue(value));
            if (changes["remove"] is JsonArray remove)
                foreach (var field in remove) if (field?.GetValue<string>() is { Length: > 0 } f) write.Remove(f);
        }, ct);
    }
}

/// <summary>ctx.db: get, set, update, delete, query — documents as plain JSON both ways.</summary>
internal sealed class DatabaseBinding : IFunctionBinding
{
    public string Name => "db";

    public async Task<JsonNode?> CallAsync(string op, JsonObject args, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<DocumentDatabase>();
        try
        {
            switch (op)
            {
                case "get":
                {
                    var doc = await db.GetAsync(Path(args), ct);
                    var expand = Expand(args, services);
                    if (expand is null) return Plain.Of(doc);
                    var expansion = await ReferenceExpander.ExpandAsync(db, [doc], expand, ct, Resolution(services, db));
                    return Expanded.Of(doc, expand, expansion);
                }
                case "set":
                    await db.WriteAsync([new SetWrite { Path = Path(args), Fields = Fields(args["data"]), Merge = args["merge"]?.GetValue<bool>() == true }], ct);
                    return null;
                case "update":
                    await db.WriteAsync([new UpdateWrite
                    {
                        Path = Path(args), Fields = Fields(args["data"]).ToDictionary(kv => FieldPath.Parse(kv.Key), kv => kv.Value),
                        Precondition = new Precondition(Exists: true),
                    }], ct);
                    return null;
                case "delete":
                    if (args["recursive"]?.GetValue<bool>() == true) return await db.DeleteSubtreeAsync(Path(args), ct: ct);
                    var existed = await db.GetAsync(Path(args), ct) is not null;
                    await db.WriteAsync([new DeleteWrite { Path = Path(args) }], ct);
                    return existed ? 1 : 0;
                case "query":
                {
                    var result = await db.QueryAsync(Query(args), ct);
                    var expand = Expand(args, services);
                    if (expand is null) return new JsonArray([.. result.Documents.Select(d => (JsonNode?)Plain.Of(d))]);
                    var expansion = await ReferenceExpander.ExpandAsync(db, result.Documents, expand, ct, Resolution(services, db));
                    return new JsonArray([.. result.Documents.Select(d => (JsonNode?)Expanded.Of(d, expand, expansion))]);
                }
                default:
                    throw new FunctionCallException("invalid-argument", $"ctx.db.{op} isn't a thing.");
            }
        }
        catch (RuntimeException ex)
        {
            throw new FunctionCallException(ex.Status switch
            {
                RuntimeStatus.NotFound => "not-found", RuntimeStatus.AlreadyExists => "already-exists", RuntimeStatus.FailedPrecondition => "failed-precondition",
                RuntimeStatus.Aborted => "aborted", RuntimeStatus.DeadlineExceeded => "deadline-exceeded", _ => "invalid-argument",
            }, ex.Message);
        }
    }

    /// <summary>File and user references, resolved with the server's rights.</summary>
    private static ReferenceResolution? Resolution(IServiceProvider services, DocumentDatabase db) =>
        services.GetService<ReferenceResolvers>()?.For(db);

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

    private static IReadOnlyDictionary<string, Value> Fields(JsonNode? data) =>
        data is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => ToValue(kv.Value)) : throw new FunctionCallException("invalid-argument", "Give the fields as an object.");

    /// <summary>
    /// Plain JSON to a value: whole numbers stay integers, { $timestamp } is a timestamp, { $documentReference }, { $fileReference } and
    /// { $userReference } are references (ref.doc(), ref.file(), ref.user() in functions).
    /// </summary>
    internal static Value ToValue(JsonNode? node) => node switch
    {
        null => new NullValue(),
        JsonObject o when o.Count == 1 && o["$timestamp"] is JsonValue ts && DateTimeOffset.TryParse(ts.GetValue<string>(), out var at) => new TimestampValue(at),
        JsonObject o when o.Count == 1 && o["$documentReference"] is JsonValue d => new DocumentReferenceValue(d.GetValue<string>().Trim('/')),
        JsonObject o when o.Count == 1 && o["$fileReference"] is JsonValue f => new FileReferenceValue(f.GetValue<string>().TrimStart('/')),
        JsonObject o when o.Count == 1 && o["$userReference"] is JsonValue u => new UserReferenceValue(u.GetValue<string>()),
        JsonObject o => new MapValue(o.ToDictionary(kv => kv.Key, kv => ToValue(kv.Value))),
        JsonArray a => new ArrayValue([.. a.Select(ToValue)]),
        JsonValue v when v.TryGetValue<bool>(out var b) => new BooleanValue(b),
        JsonValue v when v.TryGetValue<long>(out var l) => new IntegerValue(l),
        JsonValue v when v.TryGetValue<double>(out var d) => d == Math.Floor(d) && Math.Abs(d) < 9e15 ? new IntegerValue((long)d) : new DoubleValue(d),
        JsonValue v => new StringValue(v.ToString()),
        _ => new NullValue(),
    };

    private static Query Query(JsonObject args)
    {
        var collection = args["collection"]?.GetValue<string>()?.Trim('/') is { Length: > 0 } c ? c : throw new FunctionCallException("invalid-argument", "Give a collection.");
        var filters = new List<Filter>();
        foreach (var w in args["where"] as JsonArray ?? [])
        {
            if (w is not JsonArray { Count: 3 } clause) throw new FunctionCallException("invalid-argument", "Each where is [field, op, value].");
            var op = clause[1]!.GetValue<string>() switch
            {
                "==" => FilterOperator.Eq, "!=" => FilterOperator.Ne, "<" => FilterOperator.Lt, "<=" => FilterOperator.Lte, ">" => FilterOperator.Gt,
                ">=" => FilterOperator.Gte, "array-contains" => FilterOperator.ArrayContains, "in" => FilterOperator.In,
                var x => throw new FunctionCallException("invalid-argument", $"'{x}' isn't an operator (==, !=, <, <=, >, >=, array-contains, in)."),
            };
            filters.Add(new FieldFilter(FieldPath.Parse(clause[0]!.GetValue<string>()), op, ToValue(clause[2])));
        }
        var orderBy = (args["orderBy"] as JsonArray ?? []).Select(o => o is JsonArray { Count: > 0 } pair
            ? new Ordering(FieldPath.Parse(pair[0]!.GetValue<string>()), pair.Count > 1 && pair[1]?.GetValue<string>() == "desc" ? SortDirection.Desc : SortDirection.Asc)
            : throw new FunctionCallException("invalid-argument", "Each orderBy is [field, \"asc\" | \"desc\"].")).ToList();
        var limit = args["limit"]?.GetValue<int>() is { } l ? Math.Clamp(l, 1, 1000) : 100;
        return new Query(collection, filters.Count switch { 0 => null, 1 => filters[0], _ => new CompositeFilter(CompositeOp.And, filters) },
            orderBy.Count == 0 ? null : orderBy, limit);
    }
}
