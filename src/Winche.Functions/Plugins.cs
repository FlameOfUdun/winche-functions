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

    public const string FileName = "winche-plugin.json";
    private static readonly Regex SettingName = new("^[A-Za-z][A-Za-z0-9_]{0,63}$");
    private static readonly Regex SecretName = new("^[A-Z][A-Z0-9_]{0,29}$");

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
}

/// <summary>A secret the plugin reads (an API key): each installed copy has its own project secret, <c>{ID}__{NAME}</c>.</summary>
public sealed record PluginSecret
{
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public bool Required { get; init; } = true;
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
        foreach (var key in given.Keys.Where(k => !manifest.Settings.ContainsKey(k)).Order(StringComparer.Ordinal))
            problems.Add($"There's no setting {key}{(manifest.Settings.Count == 0 ? "" : $" (it has {string.Join(", ", manifest.Settings.Keys)})")}.");
        foreach (var (name, s) in manifest.Settings)
        {
            var value = given.TryGetValue(name, out var v) && v is not null && !(v is JsonValue sv && sv.GetValueKind() == JsonValueKind.String && sv.GetValue<string>() == "")
                ? v : s.Default;
            if (value is null)
            {
                if (s.Required) problems.Add($"{s.Label} ({name}) is needed.");
                values[name] = null;
                continue;
            }
            if (Problem(name, s, value) is { } bad) problems.Add($"{s.Label} ({name}): {bad}");
            values[name] = value.DeepClone();
        }
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
}

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
        if (options.ProvidedPlugins.TryGetValue(name, out var folder)) return Provided(name, folder, version);
        var doc = await RegistryAsync(name, version ?? "latest", ct);
        var exact = doc["version"]?.GetValue<string>() ?? throw new FunctionDeployException($"npm has no version for {name}.", []);
        var tarball = doc["dist"]?["tarball"]?.GetValue<string>() ?? throw new FunctionDeployException($"npm gives no package file for {name}@{exact}.", []);
        var integrity = doc["dist"]?["integrity"]?.GetValue<string>();
        var bytes = await DownloadAsync(tarball, ct);
        if (integrity is { } i && i.StartsWith("sha512-", StringComparison.Ordinal)
            && !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(i[7..]), SHA512.HashData(bytes)))
            throw new FunctionDeployException($"{name}@{exact}'s package file doesn't match npm's integrity hash.", []);
        var manifest = await ManifestInTarballAsync(bytes, ct)
            ?? throw new FunctionDeployException($"{name}@{exact} isn't a Winche plugin: it has no {PluginManifest.FileName}.", []);
        return new PluginPackage(name, exact, PluginManifest.Parse(manifest, $"{name}@{exact}"), null);
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
        return new PluginPackage(name, have, PluginManifest.Parse(File.ReadAllText(file), $"{name}@{have}"), folder);
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

    /// <summary>The winche-plugin.json in an npm package file (a .tgz whose files are under package/).</summary>
    internal static async Task<string?> ManifestInTarballAsync(byte[] tgz, CancellationToken ct)
    {
        await using var gz = new GZipStream(new MemoryStream(tgz), CompressionMode.Decompress);
        await using var tar = new TarReader(gz);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            var parts = entry.Name.Split('/', 2);
            if (parts.Length == 2 && parts[1] == PluginManifest.FileName && entry.DataStream is { } data)
            {
                if (entry.Length > 256 * 1024) throw new FunctionDeployException($"{PluginManifest.FileName} is larger than 256 KB.", []);
                using var reader = new StreamReader(data);
                return await reader.ReadToEndAsync(ct);
            }
        }
        return null;
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
