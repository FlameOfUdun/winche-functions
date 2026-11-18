using System.Text.RegularExpressions;

namespace Winche.Functions;

/// <summary>A file of a console page, as the console serves it.</summary>
public sealed record ConsolePageFile(byte[] Content, string ContentType);

/// <summary>
/// Console pages (<c>consolePage</c>): the project's own HTML, JS and CSS, deployed with the functions, which the console
/// shows in a sandboxed frame. In them @winche-technologies/winche-admin acts as the person who opened the page, limited by their role.
/// </summary>
public static class ConsolePages
{
    internal static readonly Regex IconName = new("^[a-z0-9]+(-[a-z0-9]+)*$");

    /// <summary>Binary files in a deployment are sent as text with this prefix and the bytes in base64.</summary>
    public const string Base64Prefix = "base64:";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8", [".svg"] = "image/svg+xml", [".txt"] = "text/plain; charset=utf-8",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".ico"] = "image/x-icon", [".woff"] = "font/woff", [".woff2"] = "font/woff2",
    };

    /// <summary>The extensions a page may have (the CLI deploys these).</summary>
    public static IReadOnlyCollection<string> Extensions => Types.Keys;

    /// <summary>Binary extensions, deployed as <see cref="Base64Prefix"/> text.</summary>
    public static readonly IReadOnlySet<string> Binary = new HashSet<string>([".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".woff", ".woff2"], StringComparer.OrdinalIgnoreCase);

    /// <summary>viewer 0, member 1, admin 2; -1 for anything else.</summary>
    public static int Rank(string? role) => role?.ToLowerInvariant() switch { "viewer" => 0, "member" => 1, "admin" => 2, _ => -1 };

    /// <summary>A page's folder made plain ("./console/a/" → "console/a"), or null when it leaves the deployment.</summary>
    internal static string? Folder(string given)
    {
        var parts = given.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        return parts.Count == 0 || parts.Any(p => p == ".." || p.StartsWith('.')) ? null : string.Join('/', parts);
    }

    /// <summary>The console pages of a deployment that <paramref name="role"/> may see, in sidebar order.</summary>
    public static IReadOnlyList<FunctionInfo> For(Deployment? deployment, string role) =>
        [.. (deployment?.Functions ?? []).Where(f => f is { Trigger.Kind: "consolePage", Page: not null } && Rank(role) >= Rank(f.MinRole ?? "viewer"))
            .OrderBy(f => f.Page!.Order).ThenBy(f => f.Page!.Title, StringComparer.OrdinalIgnoreCase)];

    /// <summary>File <paramref name="path"/> of page <paramref name="page"/> (index.html for ""), or null.</summary>
    public static ConsolePageFile? File(Deployment deployment, FunctionInfo page, string path)
    {
        var rel = path.Length == 0 || path.EndsWith('/') ? path + "index.html" : path;
        if (Folder(rel) is not { } clean || page.Page is null) return null;
        if (!deployment.Files.TryGetValue($"{page.Page.Files}/{clean}", out var text)) return null;
        var type = Types.GetValueOrDefault(Path.GetExtension(clean), "application/octet-stream");
        byte[] bytes;
        try { bytes = text.StartsWith(Base64Prefix, StringComparison.Ordinal) ? Convert.FromBase64String(text[Base64Prefix.Length..]) : System.Text.Encoding.UTF8.GetBytes(text); }
        catch (FormatException) { return null; }
        return new ConsolePageFile(bytes, type);
    }
}
