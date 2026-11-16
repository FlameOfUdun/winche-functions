using System.Text.Json;
using System.Text.Json.Nodes;

namespace Winche.Functions;

/// <summary>
/// A sample value for a JSON Schema, to start from when running a function by hand: the schema's own <c>examples</c>,
/// <c>default</c> or <c>const</c> when it has one, else the first <c>enum</c> value, else a plain value of its type (an
/// object with its required properties, or all of them when none are listed; an array of one item).
/// </summary>
public static class SchemaExample
{
    public static JsonNode? Of(JsonNode? schema) => Of(schema, 0);

    private static JsonNode? Of(JsonNode? schema, int depth)
    {
        if (schema is not JsonObject s || depth > 8) return null;
        if (s["examples"] is JsonArray { Count: > 0 } examples) return examples[0]?.DeepClone();
        if (s.ContainsKey("default")) return s["default"]?.DeepClone();
        if (s.ContainsKey("const")) return s["const"]?.DeepClone();
        if (s["enum"] is JsonArray { Count: > 0 } values) return values[0]?.DeepClone();
        foreach (var key in new[] { "oneOf", "anyOf", "allOf" })
            if (s[key] is JsonArray { Count: > 0 } options) return Of(options[0], depth + 1);
        var type = s["type"] switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonArray types => types.Select(t => t?.GetValue<string>()).FirstOrDefault(t => t != "null") ?? "null",
            _ => s.ContainsKey("properties") ? "object" : s.ContainsKey("items") ? "array" : null,
        };
        switch (type)
        {
            case "object":
                var result = new JsonObject();
                var properties = s["properties"] as JsonObject ?? [];
                var required = (s["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).OfType<string>().ToHashSet();
                foreach (var (name, property) in properties)
                    if (required is null || required.Count == 0 || required.Contains(name)) result[name] = Of(property, depth + 1);
                return result;
            case "array":
                return new JsonArray(Of(s["items"], depth + 1));
            case "string":
                return s["format"]?.GetValue<string>() switch
                {
                    "date-time" => "2026-01-01T09:00:00Z", "date" => "2026-01-01", "email" => "ann@example.com", "uri" or "url" => "https://example.com",
                    "uuid" => "00000000-0000-0000-0000-000000000000", _ => "",
                };
            case "integer":
            case "number":
                return s["minimum"]?.DeepClone() ?? JsonValue.Create(0);
            case "boolean":
                return false;
            default:
                return null;
        }
    }
}
