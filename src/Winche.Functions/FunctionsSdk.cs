namespace Winche.Functions;

/// <summary>The @winche/functions module (TypeScript): what deployments import, and what editors use for types.</summary>
public static class FunctionsSdk
{
    /// <summary>The module's source, to save as .winche/functions.ts next to the functions.</summary>
    public static string Source => DenoRuntime.Resource("functions.ts");

    /// <summary>The deno.json a functions folder starts with: @winche/functions resolved to that file.</summary>
    public const string DenoJson = """
        {
          "imports": {
            "@winche/functions": "./.winche/functions.ts"
          }
        }
        """;

    /// <summary>The Deno release Winche runs and tests with.</summary>
    public const string DenoVersion = "2.9.8";
}
