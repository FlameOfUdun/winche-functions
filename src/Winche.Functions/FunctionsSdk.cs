namespace Winche.Functions;

/// <summary>
/// The TypeScript SDKs functions are written with, which each project pins itself (npm): <c>@winche-technologies/winche-functions</c>
/// (defining functions) and <c>@winche-technologies/winche-admin</c> (the project's data). The server doesn't bring them: it bundles what
/// the deployment's deno.json names, unless the host provides them (<see cref="FunctionsOptions.ProvidedPackages"/>).
/// </summary>
public static class FunctionsSdk
{
    /// <summary>The protocol this server speaks with the SDKs (their exported functions' <c>__winche</c>, @winche-technologies/winche-admin's calls).</summary>
    public const int Protocol = 1;

    /// <summary>
    /// What a callable that only the project's apps may call (<c>integrity: true</c>) is marked with instead of
    /// <see cref="Protocol"/>: servers from before Integrity don't know the option, so they refuse the deployment
    /// rather than let anyone call it. Everything else is the same as <see cref="Protocol"/>.
    /// </summary>
    public const int IntegrityProtocol = 2;

    /// <summary>The SDKs' major version for <see cref="Protocol"/>.</summary>
    public const int Major = 3;

    /// <summary>
    /// The packages a functions folder imports, by their npm names → their npm specifiers (for deno.json's imports).
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Specifier)> Packages =
    [
        ("@winche-technologies/winche-functions", $"npm:@winche-technologies/winche-functions@^{Major}"),
        ("@winche-technologies/winche-functions/testing", $"npm:@winche-technologies/winche-functions@^{Major}/testing"),
        ("@winche-technologies/winche-admin", $"npm:@winche-technologies/winche-admin@^{Major}"),
    ];

    /// <summary>
    /// The SDK version plugins need at least (plugin() came in 3.1): the deployment's entry asks for it, so a server
    /// whose npm cache knows only older ones looks again instead of building plugins against one without it.
    /// </summary>
    public const string PluginSdk = "npm:@winche-technologies/winche-functions@^3.1.0";

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
