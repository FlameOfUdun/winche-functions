namespace Winche.Functions;

/// <summary>
/// The TypeScript SDKs functions are written with, which each project pins itself (npm): <c>@winche/functions</c>
/// (defining functions) and <c>@winche/admin</c> (the project's data). The server doesn't bring them: it bundles what
/// the deployment's deno.json names, unless the host provides them (<see cref="FunctionsOptions.ProvidedPackages"/>).
/// </summary>
public static class FunctionsSdk
{
    /// <summary>The protocol this server speaks with the SDKs (their exported functions' <c>__winche</c>, @winche/admin's calls).</summary>
    public const int Protocol = 1;

    /// <summary>The SDKs' major version for <see cref="Protocol"/>.</summary>
    public const int Major = 3;

    /// <summary>
    /// The packages a functions folder imports: the name code imports (<c>@winche/…</c>) → its npm specifier (for
    /// deno.json's imports). They're published under the <c>@winche-technologies</c> scope; <c>@winche/…</c> is the alias.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Specifier)> Packages =
    [
        ("@winche/functions", $"npm:@winche-technologies/winche-functions@^{Major}"),
        ("@winche/functions/testing", $"npm:@winche-technologies/winche-functions@^{Major}/testing"),
        ("@winche/admin", $"npm:@winche-technologies/winche-admin@^{Major}"),
    ];

    /// <summary>The npm packages behind <see cref="Packages"/>.</summary>
    public static readonly IReadOnlyList<string> NpmPackages = ["npm:@winche-technologies/winche-functions", "npm:@winche-technologies/winche-admin"];

    /// <summary>
    /// The deno.json a functions folder starts with: the SDKs, from npm, exempt from Deno's wait for new versions (a
    /// day, against compromised releases) so a new SDK can be used at once; every other package keeps it.
    /// </summary>
    public static string DenoJson =>
        "{\n  \"imports\": {\n" + string.Join(",\n", Packages.Select(p => $"    \"{p.Name}\": \"{p.Specifier}\"")) + "\n  },\n"
        + "  \"minimumDependencyAge\": {\n    \"age\": \"P1D\",\n    \"exclude\": [" + string.Join(", ", NpmPackages.Select(p => $"\"{p}\"")) + "]\n  }\n}";

    /// <summary>The Deno release Winche runs and tests with.</summary>
    public const string DenoVersion = "2.9.8";
}
