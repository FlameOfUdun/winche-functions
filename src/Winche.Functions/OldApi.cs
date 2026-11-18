using System.Text.RegularExpressions;

namespace Winche.Functions;

/// <summary>
/// The functions API before @winche-technologies/winche-admin (data on ctx, the old trigger names, console actions): a deployment that
/// still uses it is refused, each use named with what replaces it.
/// </summary>
public static partial class OldApi
{
    private static readonly (Regex Pattern, Func<Match, string> Instead)[] Uses =
    [
        (Ctx(), m => m.Groups[1].Value switch
        {
            "documents" or "files" or "auth" or "assets" => $"import {{ {m.Groups[1].Value} }} from \"@winche-technologies/winche-admin\" instead of ctx.{m.Groups[1].Value}",
            "secrets" => "declare the secret once (const key = secrets.define(\"NAME\") from @winche-technologies/winche-functions), list it in the function's options ({ secrets: [key] }) and read key.value()",
            "resources" => "const file = resources.define(\"path\") from @winche-technologies/winche-functions, then await file.bytes() / file.text(), or resources.list(folder)",
            "console" => "console actions are gone: a page acts as its viewer with @winche-technologies/winche-admin",
            _ => "ctx.name instead of ctx.functionName",
        }),
        (Trigger(), m => $"{New(m.Groups[1].Value, m.Groups[2].Value)} (from @winche-technologies/winche-functions) instead of {m.Groups[1].Value}.{m.Groups[2].Value}"),
        (ConsolePage(), _ => "pages({ title, files }) instead of consolePage"),
        (ConsoleAction(), _ => "console actions are gone: a page reads and writes with @winche-technologies/winche-admin as its viewer; long work is asked for with a document a trigger runs on"),
    ];

    /// <summary>Each use of the old API in the deployment's code: "file:line: what to use instead".</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyDictionary<string, string> files)
    {
        var problems = new List<string>();
        // Packages carried in the folder (a copy of an SDK, say) aren't the project's code.
        var packages = files.Keys.Where(k => k == "package.json" || k.EndsWith("/package.json", StringComparison.Ordinal))
            .Select(k => k[..^"package.json".Length]).ToList();
        foreach (var (name, text) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!(name.EndsWith(".ts") || name.EndsWith(".tsx") || name.EndsWith(".js") || name.EndsWith(".mjs")) || name.StartsWith(".winche/", StringComparison.Ordinal)
                || name.EndsWith(".d.ts", StringComparison.Ordinal) || packages.Any(p => p.Length > 0 && name.StartsWith(p, StringComparison.Ordinal))) continue;
            foreach (var (pattern, instead) in Uses)
                foreach (Match m in pattern.Matches(text))
                {
                    if (problems.Count >= 30) return problems;
                    var line = 1 + text.AsSpan(0, m.Index).Count('\n');
                    problems.Add($"{name}:{line}: {instead(m)}.");
                }
        }
        return problems;
    }

    private static string New(string group, string trigger) => (group, trigger) switch
    {
        ("documents", "beforeWrite") => "onDocument.willBeWritten",
        ("files", "beforeWrite") => "onFile.willBeWritten",
        ("users", "beforeCreate") => "onUser.willBeCreated",
        ("users", "beforeSignIn") => "onUser.willSignIn",
        _ => $"{group switch { "documents" => "onDocument", "files" => "onFile", "users" => "onUser", _ => "onAssets" }}.{char.ToLowerInvariant(trigger[2])}{trigger[3..]}",
    };

    [GeneratedRegex(@"\bctx\.(documents|files|auth|assets|secrets|resources|console|functionName)\b")]
    private static partial Regex Ctx();

    [GeneratedRegex(@"\b(documents|files|users|assets)\.(onCreated|onUpdated|onDeleted|onWritten|beforeWrite|onUploaded|onMetadataUpdated|onSignedIn|onDisabled|beforeCreate|beforeSignIn|onReleased|onChannelMoved)\s*\(")]
    private static partial Regex Trigger();

    [GeneratedRegex(@"\bconsolePage\s*\(")]
    private static partial Regex ConsolePage();

    [GeneratedRegex(@"\bconsoleAction\s*[<(]")]
    private static partial Regex ConsoleAction();
}
