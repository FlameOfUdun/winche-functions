using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Winche.Functions;

/// <summary>
/// A plugin package's <c>winche-plugin.json</c>: what the console shows before installing it, the settings an installer
/// gives, the secrets it needs, and what it does with the project's data.
/// </summary>
public sealed record PluginManifest
{
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>A Tabler icon name.</summary>
    public string? Icon { get; init; }
    public IReadOnlyDictionary<string, PluginSetting> Settings { get; init; } = new Dictionary<string, PluginSetting>();
    public IReadOnlyDictionary<string, PluginSecret> Secrets { get; init; } = new Dictionary<string, PluginSecret>();
    /// <summary>What it reads, writes and calls, a sentence each: shown before it's installed or upgraded.</summary>
    public IReadOnlyList<string> Access { get; init; } = [];
    /// <summary>What it may cost, beside the server (another service's charges): shown before it's installed.</summary>
    public string? Billing { get; init; }
    /// <summary>
    /// What to do after installing it (add a webhook in the other service, point a number at it), a step each, shown on
    /// the installed copy's page with <c>{{function:name}}</c> (that function's address), <c>{{setting:NAME}}</c>,
    /// <c>{{secret:NAME}}</c> (the copy's project secret) and <c>{{id}}</c> filled in.
    /// </summary>
    public IReadOnlyList<PluginStep> Setup { get; init; } = [];

    public const string FileName = "winche-plugin.json";
    private static readonly Regex SettingName = new("^[A-Za-z][A-Za-z0-9_]{0,63}$");
    private static readonly Regex SecretName = new("^[A-Z][A-Z0-9_]{0,29}$");
    private static readonly Regex Placeholder = new(@"\{\{(function|setting|secret):([A-Za-z0-9_]+)\}\}");

    public static PluginManifest Parse(string json, string package)
    {
        PluginManifest? m;
        try { m = JsonSerializer.Deserialize<PluginManifest>(json, FunctionStore.Json); }
        catch (JsonException ex) { throw new FunctionDeployException($"{package}'s {FileName} isn't valid JSON.", [ex.Message]); }
        if (m is null) throw new FunctionDeployException($"{package}'s {FileName} is empty.", []);
        if (m.Problems() is { Count: > 0 } problems) throw new FunctionDeployException($"{package}'s {FileName} isn't right.", problems);
        return m;
    }

    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (Title.Trim().Length is 0 or > 60) p.Add("Give it a title (up to 60 characters).");
        if (Description is { Length: > 500 }) p.Add("The description is up to 500 characters.");
        if (Icon is not null && !ConsolePages.IconName.IsMatch(Icon)) p.Add("icon is a Tabler icon name, e.g. \"message\".");
        foreach (var (name, s) in Settings)
        {
            if (!SettingName.IsMatch(name)) p.Add($"settings: '{name}' isn't a setting name (letters, digits and _).");
            if (s.Label.Trim().Length is 0 or > 80) p.Add($"settings.{name}: give it a label (up to 80 characters).");
            if (s.Type is not ("string" or "number" or "integer" or "boolean" or "select")) p.Add($"settings.{name}: type is string, number, integer, boolean or select.");
            if (s.Type == "select" && s.Options is not { Count: > 0 }) p.Add($"settings.{name}: a select lists its options.");
            if (s.Default is not null && PluginSettings.Problem(name, s, s.Default) is { } bad) p.Add($"settings.{name}: its default: {bad}");
        }
        foreach (var (name, s) in Secrets)
        {
            if (!SecretName.IsMatch(name)) p.Add($"secrets: '{name}' isn't a secret name (capitals, digits and _, up to 30).");
            if (s.Label.Trim().Length is 0 or > 80) p.Add($"secrets.{name}: give it a label (up to 80 characters).");
        }
        if (Access.Any(a => a.Trim().Length is 0 or > 300)) p.Add("access lists sentences of up to 300 characters.");
        if (Billing is { Length: > 300 }) p.Add("billing is a sentence of up to 300 characters.");
        void Condition(string where, PluginCondition? c)
        {
            if (c is null) return;
            if (!Settings.ContainsKey(c.Setting)) p.Add($"{where}: showIf names {c.Setting}, which isn't one of its settings.");
            if (c.Is is null && c.Set is null) p.Add($"{where}: showIf says is (a value, or a list) or set (true or false).");
        }
        foreach (var (name, s) in Settings)
        {
            Condition($"settings.{name}", s.ShowIf);
            if (s.Section is { } section && section.Trim().Length is 0 or > 40) p.Add($"settings.{name}: section is a name of up to 40 characters.");
        }
        foreach (var (name, s) in Secrets) Condition($"secrets.{name}", s.ShowIf);
        for (var i = 0; i < Setup.Count; i++)
        {
            if (Setup[i].Text.Trim().Length is 0 or > 1000) p.Add($"setup[{i}]: a step is up to 1000 characters.");
            Condition($"setup[{i}]", Setup[i].ShowIf);
            foreach (Match m in Placeholder.Matches(Setup[i].Text))
            {
                var (kind, what) = (m.Groups[1].Value, m.Groups[2].Value);
                if (kind == "setting" && !Settings.ContainsKey(what)) p.Add($"setup[{i}]: {{{{setting:{what}}}}} isn't one of its settings.");
                if (kind == "secret" && !Secrets.ContainsKey(what)) p.Add($"setup[{i}]: {{{{secret:{what}}}}} isn't one of its secrets.");
            }
        }
        if (Setup.Count > 20) p.Add("setup has at most 20 steps.");
        return p;
    }
}

/// <summary>A setting an installer gives: <c>string</c>, <c>number</c>, <c>integer</c>, <c>boolean</c> or <c>select</c> (one of its options).</summary>
public sealed record PluginSetting
{
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public string Type { get; init; } = "string";
    public bool Required { get; init; }
    public JsonNode? Default { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    /// <summary>The group it's shown in (e.g. "Calls"): settings of one section are shown together, in their order.</summary>
    public string? Section { get; init; }
    /// <summary>Shown (and needed, when required) only when another setting has a value: { setting, is } or { setting, set }.</summary>
    public PluginCondition? ShowIf { get; init; }
}

/// <summary>
/// When a setting, secret or setup step applies: when <see cref="Setting"/> is <see cref="Is"/> (a value, or any of a
/// list), or, with <see cref="Set"/>, when it's set (not empty, not false) or not.
/// </summary>
public sealed record PluginCondition
{
    public string Setting { get; init; } = "";
    public JsonNode? Is { get; init; }
    public bool? Set { get; init; }

    public bool Holds(IReadOnlyDictionary<string, JsonNode?> values)
    {
        var v = values.TryGetValue(Setting, out var x) ? x : null;
        if (Set is { } wanted)
        {
            var set = v is not null && !(v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "") && v.GetValueKind() != JsonValueKind.False;
            if (set != wanted) return false;
        }
        if (Is is null) return true;
        if (v is null) return false;
        return Is is JsonArray any ? any.Any(a => JsonNode.DeepEquals(a, v)) : JsonNode.DeepEquals(Is, v);
    }
}

/// <summary>A step to do after installing (shown on the installed copy's page), when its condition holds.</summary>
public sealed record PluginStep
{
    public string Text { get; init; } = "";
    public PluginCondition? ShowIf { get; init; }
}

/// <summary>A secret the plugin reads (an API key): each installed copy has its own project secret, <c>{ID}__{NAME}</c>.</summary>
public sealed record PluginSecret
{
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public bool Required { get; init; } = true;
    /// <summary>Asked for (and needed, when required) only when this holds.</summary>
    public PluginCondition? ShowIf { get; init; }
}

/// <summary>
/// An installed copy of a plugin: the package and exact version it runs, its settings, and where it was installed from
/// (<c>console</c>, or <c>deploy</c> when winche.json lists it). Part of each deployment, so rolling back brings back the
/// plugins of the version rolled back to.
/// </summary>
public sealed record PluginInstall(string Id, string Package, string Version, IReadOnlyDictionary<string, JsonNode?> Settings, PluginManifest Manifest,
    string Source, string? By, DateTimeOffset At)
{
    /// <summary>The project secret a plugin secret is for this copy: <c>SMS__AUTH_TOKEN</c> for AUTH_TOKEN of "sms".</summary>
    public string SecretFor(string name) => PluginIds.SecretPrefix(Id) + name;

    /// <summary>Its secrets by the plugin's names → the project's.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> SecretNames => Manifest.Secrets.Keys.ToDictionary(n => n, SecretFor);

    /// <summary>Whether it's the same as <paramref name="other"/> but for when and by whom.</summary>
    public bool SameAs(PluginInstall other) => Id == other.Id && Package == other.Package && Version == other.Version
        && JsonSerializer.Serialize(Settings, FunctionStore.Json) == JsonSerializer.Serialize(other.Settings, FunctionStore.Json);
}

/// <summary>A plugin to install, as asked (the console, the CLI, winche.json): no version means the newest.</summary>
public sealed record PluginRequest(string Id, string Package, string? Version = null, IReadOnlyDictionary<string, JsonNode?>? Settings = null);

public static class PluginIds
{
    private static readonly Regex Rule = new("^[a-z](?:[a-z0-9-]{0,28}[a-z0-9])?$");

    public static string? Problem(string? id) => id is not null && Rule.IsMatch(id) ? null
        : $"'{id}' isn't a plugin id: lowercase letters, digits and -, starting with a letter (up to 30, e.g. sms).";

    public static string SecretPrefix(string id) => id.ToUpperInvariant().Replace('-', '_') + "__";

    private static readonly Regex PackageRule = new("^(?:@[a-z0-9][a-z0-9._~-]*/)?[a-z0-9][a-z0-9._~-]*$");
    private static readonly Regex VersionRule = new(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$");

    public static string? PackageProblem(string? package) => package is { Length: <= 214 } && PackageRule.IsMatch(package) ? null
        : $"'{package}' isn't an npm package name.";

    public static string? VersionProblem(string? version) => version is not null && VersionRule.IsMatch(version) ? null
        : $"'{version}' isn't an exact version (e.g. 1.2.0).";
}

/// <summary>Plugin settings checked against the manifest, with defaults filled in.</summary>
public static class PluginSettings
{
    public static IReadOnlyDictionary<string, JsonNode?> Resolve(PluginManifest manifest, IReadOnlyDictionary<string, JsonNode?>? given, out List<string> problems)
    {
        problems = [];
        given ??= new Dictionary<string, JsonNode?>();
        var values = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var key in given.Keys.Where(k => !manifest.Settings.ContainsKey(k)).Order(StringComparer.Ordinal))
            problems.Add($"There's no setting {key}{(manifest.Settings.Count == 0 ? "" : $" (it has {string.Join(", ", manifest.Settings.Keys)})")}.");
        foreach (var (name, s) in manifest.Settings)
        {
            // Not given: the default. Given empty (""): empty, for a setting that may be (a feature turned off), else needed.
            var given0 = given.TryGetValue(name, out var v) ? v : null;
            var empty = given0 is JsonValue sv && sv.GetValueKind() == JsonValueKind.String && sv.GetValue<string>() == "";
            if (empty && !s.Required && s.Type == "string")
            {
                values[name] = JsonValue.Create("");
                continue;
            }
            var value = given0 is not null && !empty ? given0 : s.Default;
            if (value is null)
            {
                if (s.Required) missing.Add(name);
                values[name] = null;
                continue;
            }
            if (Problem(name, s, value) is { } bad) problems.Add($"{s.Label} ({name}): {bad}");
            values[name] = value.DeepClone();
        }
        // A required setting is needed only where it's shown (its showIf holds with the others' values).
        foreach (var name in missing)
            if (manifest.Settings[name].ShowIf?.Holds(values) != false) problems.Add($"{manifest.Settings[name].Label} ({name}) is needed.");
        return values;
    }

    internal static string? Problem(string name, PluginSetting s, JsonNode value)
    {
        var kind = value.GetValueKind();
        return s.Type switch
        {
            "string" => kind != JsonValueKind.String ? "a text." : value.GetValue<string>().Length > 2000 ? "at most 2000 characters." : null,
            "number" => kind != JsonValueKind.Number ? "a number." : null,
            "integer" => kind != JsonValueKind.Number || double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) is var n && n != Math.Floor(n) ? "a whole number." : null,
            "boolean" => kind is not (JsonValueKind.True or JsonValueKind.False) ? "true or false." : null,
            "select" => kind != JsonValueKind.String || s.Options?.Contains(value.GetValue<string>()) != true ? $"one of {string.Join(", ", s.Options ?? [])}." : null,
            _ => null,
        };
    }
}

/// <summary>
/// A plugin package found: its exact version and manifest, and where its code comes from: a folder on this server
/// (<see cref="FunctionsOptions.ProvidedPlugins"/>), else npm (Folder null).
/// </summary>
public sealed record PluginPackage(string Name, string Version, PluginManifest Manifest, string? Folder)
{
    /// <summary>What the deployment imports it as.</summary>
    public string Specifier => $"npm:{Name}@{Version}";

    /// <summary>What its package.json and README say about it.</summary>
    public PluginAbout About { get; init; } = new();
}

/// <summary>A plugin package as its package.json and README describe it (for the console's page about it).</summary>
public sealed record PluginAbout
{
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public string? Repository { get; init; }
    public string? License { get; init; }
    /// <summary>Its README (Markdown), up to 200 KB.</summary>
    public string? Readme { get; init; }

    internal static PluginAbout Of(string? packageJson, string? readme)
    {
        JsonNode? pkg = null;
        try { pkg = packageJson is null ? null : JsonNode.Parse(packageJson); } catch (JsonException) { }
        static string? Text(JsonNode? n, string objectKey) => n switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonObject o => o[objectKey] is JsonValue x && x.GetValueKind() == JsonValueKind.String ? x.GetValue<string>() : null,
            _ => null,
        };
        var repo = Text(pkg?["repository"], "url")?.Replace("git+", "").Replace(".git", "");
        return new PluginAbout
        {
            Description = Text(pkg?["description"], ""), Author = Text(pkg?["author"], "name"), Homepage = Text(pkg?["homepage"], ""),
            Repository = repo, License = Text(pkg?["license"], "type"),
            Readme = readme is { Length: > 200_000 } long_ ? long_[..200_000] : readme,
        };
    }
}

/// <summary>A version of a plugin package, and when it was published (null for a server's folder).</summary>
public sealed record PluginVersion(string Version, DateTimeOffset? At);

/// <summary>A plugin package a search found.</summary>
public sealed record PluginSearchResult(string Package, string Version, string? Description, string? Publisher, DateTimeOffset? At, bool Provided);

/// <summary>The HTTP client plugin packages are looked up with (tests give their own handler).</summary>
public sealed class PluginRegistryHttp(HttpClient http)
{
    public HttpClient Client => http;
}

/// <summary>
/// Finds plugin packages: folders this server provides (<see cref="FunctionsOptions.ProvidedPlugins"/>), else the npm
/// registry (<see cref="FunctionsOptions.PluginRegistry"/>), whose package is checked against its integrity hash and read
/// for its winche-plugin.json.
/// </summary>
public sealed class PluginPackages(FunctionsOptions options, PluginRegistryHttp http)
{
    private const long MaxPackageBytes = 20L * 1024 * 1024;

    /// <summary>The package at <paramref name="version"/>, or its newest when null.</summary>
    public async Task<PluginPackage> GetAsync(string name, string? version, CancellationToken ct = default)
    {
        if (PluginIds.PackageProblem(name) is { } bad) throw new FunctionDeployException(bad, []);
        if (version is not null && PluginIds.VersionProblem(version) is { } badVersion) throw new FunctionDeployException(badVersion, []);
        // A server folder: this exact version's (name@version), else the one it provides by name.
        if (version is not null && options.ProvidedPlugins.TryGetValue($"{name}@{version}", out var exactFolder)) return Provided(name, exactFolder, version);
        if (options.ProvidedPlugins.TryGetValue(name, out var folder)) return Provided(name, folder, version);
        var doc = await RegistryAsync(name, version ?? "latest", ct);
        var exact = doc["version"]?.GetValue<string>() ?? throw new FunctionDeployException($"npm has no version for {name}.", []);
        var tarball = doc["dist"]?["tarball"]?.GetValue<string>() ?? throw new FunctionDeployException($"npm gives no package file for {name}@{exact}.", []);
        var integrity = doc["dist"]?["integrity"]?.GetValue<string>();
        var bytes = await DownloadAsync(tarball, ct);
        if (integrity is { } i && i.StartsWith("sha512-", StringComparison.Ordinal)
            && !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(i[7..]), SHA512.HashData(bytes)))
            throw new FunctionDeployException($"{name}@{exact}'s package file doesn't match npm's integrity hash.", []);
        var files = await FilesInTarballAsync(bytes, ct);
        var manifest = files.GetValueOrDefault(PluginManifest.FileName)
            ?? throw new FunctionDeployException($"{name}@{exact} isn't a Winche plugin: it has no {PluginManifest.FileName}.", []);
        return new PluginPackage(name, exact, PluginManifest.Parse(manifest, $"{name}@{exact}"), null)
        {
            About = PluginAbout.Of(files.GetValueOrDefault("package.json"), files.GetValueOrDefault("README.md")),
        };
    }

    /// <summary>The newest version of <paramref name="name"/>, or null when it can't be found.</summary>
    public async Task<string?> LatestAsync(string name, CancellationToken ct = default)
    {
        if (options.ProvidedPlugins.TryGetValue(name, out var folder)) return VersionOf(folder);
        try { return (await RegistryAsync(name, "latest", ct))["version"]?.GetValue<string>(); }
        catch (FunctionDeployException) { return null; }
        catch (HttpRequestException) { return null; }
    }

    private static PluginPackage Provided(string name, string folder, string? version)
    {
        var have = VersionOf(folder);
        if (version is not null && version != have) throw new FunctionDeployException($"This server provides {name} {have}, not {version}.", []);
        var file = Path.Combine(folder, PluginManifest.FileName);
        if (!File.Exists(file)) throw new FunctionDeployException($"{name} isn't a Winche plugin: it has no {PluginManifest.FileName}.", []);
        var readme = Directory.EnumerateFiles(folder).FirstOrDefault(f => Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase));
        return new PluginPackage(name, have, PluginManifest.Parse(File.ReadAllText(file), $"{name}@{have}"), folder)
        {
            About = PluginAbout.Of(File.ReadAllText(Path.Combine(folder, "package.json")), readme is null ? null : File.ReadAllText(readme)),
        };
    }

    /// <summary>The versions of <paramref name="name"/>, newest first (at most 30).</summary>
    public async Task<IReadOnlyList<PluginVersion>> VersionsAsync(string name, CancellationToken ct = default)
    {
        if (PluginIds.PackageProblem(name) is { } bad) throw new FunctionDeployException(bad, []);
        var provided = options.ProvidedPlugins.Where(kv => kv.Key == name || kv.Key.StartsWith(name + "@", StringComparison.Ordinal))
            .Select(kv => VersionOf(kv.Value)).Distinct().ToList();
        if (provided.Count > 0) return [.. provided.OrderByDescending(SortKey).Select(v => new PluginVersion(v, null))];
        using var r = await http.Client.GetAsync($"{options.PluginRegistry.TrimEnd('/')}/{name.Replace("/", "%2F")}", ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) throw new FunctionDeployException($"npm has no package {name}.", []);
        if (!r.IsSuccessStatusCode) throw new FunctionDeployException($"npm answered {(int)r.StatusCode} for {name}.", []);
        var doc = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];
        var times = doc["time"] as JsonObject ?? [];
        return [.. (doc["versions"] as JsonObject ?? []).Select(v => v.Key)
            .Select(v => new PluginVersion(v, times[v] is JsonValue t && DateTimeOffset.TryParse(t.GetValue<string>(), out var at) ? at : null))
            .OrderByDescending(v => v.At ?? DateTimeOffset.MinValue).ThenByDescending(v => SortKey(v.Version)).Take(30)];
    }

    /// <summary>
    /// Plugin packages matching <paramref name="text"/>: this server's folders, and npm packages with the keyword
    /// winche-plugin (a registry without search: the folders only).
    /// </summary>
    public async Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string? text, CancellationToken ct = default)
    {
        var q = (text ?? "").Trim();
        var list = options.ProvidedPlugins.Where(kv => !kv.Key.Contains('@', StringComparison.Ordinal) || kv.Key.LastIndexOf('@') == 0)
            .Where(kv => q.Length == 0 || kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(kv =>
            {
                var pkg = Provided(kv.Key, kv.Value, null);
                return new PluginSearchResult(kv.Key, pkg.Version, pkg.Manifest.Description ?? pkg.About.Description, pkg.About.Author, null, true);
            }).ToList();
        try
        {
            var url = $"{options.PluginRegistry.TrimEnd('/')}/-/v1/search?size=20&text={Uri.EscapeDataString(("keywords:winche-plugin " + q).Trim())}";
            using var r = await http.Client.GetAsync(url, ct);
            if (r.IsSuccessStatusCode && JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))?["objects"] is JsonArray found)
                foreach (var o in found.OfType<JsonObject>())
                {
                    var p = o["package"] as JsonObject ?? [];
                    var name = p["name"]?.GetValue<string>();
                    if (name is null || list.Any(x => x.Package == name)) continue;
                    list.Add(new PluginSearchResult(name, p["version"]?.GetValue<string>() ?? "", p["description"]?.GetValue<string>(),
                        p["publisher"]?["username"]?.GetValue<string>(), p["date"] is JsonValue d && DateTimeOffset.TryParse(d.GetValue<string>(), out var at) ? at : null, false));
                }
        }
        catch (HttpRequestException) { }
        catch (JsonException) { }
        return list;
    }

    private static (int, int, int, string) SortKey(string v)
    {
        var parts = v.Split('-', 2);
        var n = parts[0].Split('.').Select(x => int.TryParse(x, out var i) ? i : 0).Concat([0, 0, 0]).ToArray();
        return (n[0], n[1], n[2], parts.Length > 1 ? "" : "~");   // a release after its pre-releases
    }

    private static string VersionOf(string folder) =>
        (JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "package.json")))?["version"]?.GetValue<string>()) ?? "0.0.0";

    private async Task<JsonNode> RegistryAsync(string name, string version, CancellationToken ct)
    {
        var url = $"{options.PluginRegistry.TrimEnd('/')}/{name.Replace("/", "%2F")}/{Uri.EscapeDataString(version)}";
        using var r = await http.Client.GetAsync(url, ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new FunctionDeployException(version == "latest" ? $"npm has no package {name}." : $"npm has no {name}@{version}.", []);
        if (!r.IsSuccessStatusCode) throw new FunctionDeployException($"npm answered {(int)r.StatusCode} for {name}.", []);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct)) ?? throw new FunctionDeployException($"npm answered nothing for {name}.", []);
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using var r = await http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!r.IsSuccessStatusCode) throw new FunctionDeployException($"The package file couldn't be downloaded ({(int)r.StatusCode}).", []);
        await using var body = await r.Content.ReadAsStreamAsync(ct);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (copy.Length + read > MaxPackageBytes) throw new FunctionDeployException($"The package is larger than {MaxPackageBytes / 1024 / 1024} MB.", []);
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    /// <summary>
    /// The files the console shows from an npm package file (a .tgz whose files are under package/): winche-plugin.json,
    /// package.json and README.md (by those names).
    /// </summary>
    internal static async Task<Dictionary<string, string>> FilesInTarballAsync(byte[] tgz, CancellationToken ct)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var gz = new GZipStream(new MemoryStream(tgz), CompressionMode.Decompress);
        await using var tar = new TarReader(gz);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            var parts = entry.Name.Split('/', 2);
            if (parts.Length != 2 || parts[1].Contains('/') || entry.DataStream is not { } data) continue;
            var name = parts[1].Equals("README.md", StringComparison.OrdinalIgnoreCase) ? "README.md" : parts[1];
            if (name is not (PluginManifest.FileName or "package.json" or "README.md")) continue;
            if (name == PluginManifest.FileName && entry.Length > 256 * 1024) throw new FunctionDeployException($"{PluginManifest.FileName} is larger than 256 KB.", []);
            if (entry.Length > 1024 * 1024) continue;
            using var reader = new StreamReader(data);
            found[name] = await reader.ReadToEndAsync(ct);
        }
        return found;
    }
}

/// <summary>The module that puts a deployment's own code and its plugins' installed copies together.</summary>
internal static class PluginEntry
{
    public const string File = ".winche/entry.js";

    /// <summary>
    /// The entry: everything the project's own entry exports, and each plugin's copy as <c>plugin:&lt;id&gt;</c> (the
    /// runtime names its functions <c>&lt;id&gt;.&lt;name&gt;</c>), each imported from its module.
    /// </summary>
    public static string Source(string? ownEntry, IReadOnlyList<(PluginInstall Install, string Import)> plugins)
    {
        var sb = new System.Text.StringBuilder();
        if (ownEntry is not null) sb.Append("export * from ").Append(JsonSerializer.Serialize("../" + ownEntry)).Append(";\n");
        for (var i = 0; i < plugins.Count; i++)
        {
            var (p, import) = plugins[i];
            var installation = new JsonObject
            {
                ["id"] = p.Id,
                ["settings"] = JsonSerializer.SerializeToNode(p.Settings, FunctionStore.Json),
                ["secrets"] = JsonSerializer.SerializeToNode(p.SecretNames, FunctionStore.Json),
            };
            var what = JsonSerializer.Serialize($"{p.Package}@{p.Version} isn't a Winche plugin: its default export is plugin(…) from @winche-technologies/winche-functions.");
            sb.Append("import * as m").Append(i).Append(" from ").Append(JsonSerializer.Serialize(import)).Append(";\n");
            sb.Append("const c").Append(i).Append(" = m").Append(i).Append(".default?.__wincheplugin === 1 ? m").Append(i).Append(".default.install(")
                .Append(installation.ToJsonString()).Append(") : (() => { throw new Error(").Append(what).Append("); })();\n");
            sb.Append("export { c").Append(i).Append(" as ").Append(JsonSerializer.Serialize("plugin:" + p.Id)).Append(" };\n");
        }
        return sb.ToString();
    }
}
