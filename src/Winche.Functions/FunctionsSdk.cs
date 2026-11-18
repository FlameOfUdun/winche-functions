namespace Winche.Functions;

/// <summary>
/// The SDK modules (TypeScript) deployments import, and editors use for types: @winche/functions (defining functions),
/// @winche/admin (the project's data) and @winche/functions/testing.
/// </summary>
public static class FunctionsSdk
{
    /// <summary>Each module's file (saved in .winche/ next to the functions) and the name code imports it by.</summary>
    public static readonly IReadOnlyList<(string File, string Specifier)> Modules =
    [
        ("functions.ts", "@winche/functions"),
        ("admin.ts", "@winche/admin"),
        ("testing.ts", "@winche/functions/testing"),
    ];

    /// <summary>A module's source by its file name (functions.ts, admin.ts, testing.ts).</summary>
    public static string SourceOf(string file) => DenoRuntime.Resource(file);

    /// <summary>The @winche/functions module's source.</summary>
    public static string Source => DenoRuntime.Resource("functions.ts");

    /// <summary>The deno.json a functions folder starts with: the SDK's modules resolved to .winche/.</summary>
    public const string DenoJson = """
        {
          "imports": {
            "@winche/functions": "./.winche/functions.ts",
            "@winche/admin": "./.winche/admin.ts",
            "@winche/functions/testing": "./.winche/testing.ts"
          }
        }
        """;

    /// <summary>The Deno release Winche runs and tests with.</summary>
    public const string DenoVersion = "2.9.8";
}
