using System.Text.Json.Nodes;

namespace Winche.Functions.Tests;

/// <summary>options.description, input, output and example: kept with the function, an example made from the input schema.</summary>
[Collection("postgres")]
public class DescribedTests(PostgresFixture pg)
{
    [Fact]
    public void Examples_are_made_from_input_schemas()
    {
        JsonNode? Of(string schema) => SchemaExample.Of(JsonNode.Parse(schema));
        Assert.Equal("""{"lang":"en","count":1}""", Of("""
            { "type": "object", "required": ["lang", "count"],
              "properties": { "lang": { "enum": ["en", "de"] }, "count": { "type": "integer", "minimum": 1 }, "note": { "type": "string" } } }
            """)!.ToJsonString());
        Assert.Equal("""{"a":"","b":false}""", Of("""{ "properties": { "a": { "type": "string" }, "b": { "type": "boolean" } } }""")!.ToJsonString());   // none required: all
        Assert.Equal("""[{"at":"2026-01-01T09:00:00Z"}]""", Of("""{ "type": "array", "items": { "type": "object", "properties": { "at": { "type": "string", "format": "date-time" } } } }""")!.ToJsonString());
        Assert.Equal("\"x\"", Of("""{ "type": "string", "examples": ["x"], "default": "y" }""")!.ToJsonString());
        Assert.Equal("3", Of("""{ "anyOf": [{ "type": "integer", "default": 3 }, { "type": "string" }] }""")!.ToJsonString());
        Assert.Equal("\"\"", Of("""{ "type": ["null", "string"] }""")!.ToJsonString());
        Assert.Null(Of("""{ "$ref": "#/defs/x" }"""));
    }

    [Fact]
    public async Task Functions_say_what_they_do_and_take()
    {
        if (Deno.Path is null) return;
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var d = await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = """
            import { callable } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";
            export const strings = callable(async (data: { lang: string }) => ({ hello: data.lang }), {
              description: "A language's strings.",
              input: { type: "object", properties: { lang: { enum: ["de", "en"] } }, required: ["lang"] },
              output: { type: "object", additionalProperties: { type: "string" } },
            });
            export const given = callable(async () => 1, { input: { type: "object" }, example: { lang: "zh" } });
            export const plain = callable(async () => 1);
            """ }, "Described", null);
        var strings = d.Functions.Single(f => f.Name == "strings");
        Assert.Equal("A language's strings.", strings.About);
        Assert.Equal("""{"lang":"de"}""", strings.ExampleInput!.ToJsonString());
        Assert.Equal("object", strings.Output!["type"]!.GetValue<string>());
        Assert.Equal("""{"lang":"zh"}""", d.Functions.Single(f => f.Name == "given").ExampleInput!.ToJsonString());
        Assert.Null(d.Functions.Single(f => f.Name == "plain").ExampleInput);

        // Kept with the version: read back as deployed.
        await functions.RefreshAsync(default);
        Assert.Equal("A language's strings.", functions.Current!.Functions.Single(f => f.Name == "strings").About);

        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = $$"""
            import { callable } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";
            export const a = callable(async () => 1, { description: "{{new string('x', 301)}}" });
            export const b = callable(async () => 1, { input: "a string" });
            """ }, "Bad", null));
        Assert.Contains(bad.Problems, p => p.StartsWith("a: description"));
        Assert.Contains(bad.Problems, p => p.StartsWith("b: input is a JSON Schema object"));
    }
}
