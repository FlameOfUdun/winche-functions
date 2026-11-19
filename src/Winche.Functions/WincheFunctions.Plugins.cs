using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Winche.Functions;

/// <summary>A plugin copy as the console lists it: the install, its newest version, and the secrets it still needs.</summary>
public sealed record PluginInstallInfo(PluginInstall Install, string? Latest, IReadOnlyList<string> SecretsNeeded, IReadOnlyList<string> Functions);

/// <summary>
/// Plugins: packages of functions a project installs under an id, configures and upgrades instead of writing them. Each
/// change is a new deployment (the project's own code with the plugins' copies), so it's in the change history and
/// rolls back with the code.
/// </summary>
public sealed partial class WincheFunctions
{
    private PluginPackages Packages => services.GetRequiredService<PluginPackages>();

    /// <summary>The plugins installed now.</summary>
    public IReadOnlyList<PluginInstall> Plugins => _current?.PluginList ?? [];

    internal static IReadOnlyList<string> PluginListProblems(IReadOnlyList<PluginInstall> plugins)
    {
        var problems = new List<string>();
        foreach (var p in plugins)
            if (PluginIds.Problem(p.Id) is { } bad) problems.Add(bad);
        foreach (var dup in plugins.GroupBy(p => p.Id).Where(g => g.Count() > 1)) problems.Add($"Two plugins are installed as {dup.Key}.");
        foreach (var dup in plugins.GroupBy(p => PluginIds.SecretPrefix(p.Id)).Where(g => g.Count() > 1))
            problems.Add($"{string.Join(" and ", dup.Select(p => p.Id))} would share their secrets ({dup.Key}…): give one another id.");
        if (plugins.Count > 20) problems.Add("A project has at most 20 plugins.");
        return problems;
    }

    /// <summary>
    /// The install <paramref name="request"/> asks for: its package found (the newest version unless one is given) and
    /// its settings checked against the package's winche-plugin.json. Throws with the problems.
    /// </summary>
    public async Task<PluginInstall> ResolvePluginAsync(PluginRequest request, string source, string? by, CancellationToken ct = default)
    {
        if (PluginIds.Problem(request.Id) is { } badId) throw new FunctionDeployException(badId, []);
        var package = await Packages.GetAsync(request.Package, request.Version, ct);
        var settings = PluginSettings.Resolve(package.Manifest, request.Settings, out var problems);
        if (problems.Count > 0) throw new FunctionDeployException($"{request.Id}'s settings aren't right.", problems);
        return new PluginInstall(request.Id, package.Name, package.Version, settings, package.Manifest, source, by, DateTimeOffset.UtcNow);
    }

    private async Task<Deployment> WithPluginsAsync(IReadOnlyList<PluginInstall> plugins, string note, string? by, CancellationToken ct)
    {
        if (_current is null) await RefreshAsync(ct);
        var d = _current is { } now ? await store.GetAsync(now.Version, ct) : null;
        return await DeployAsync(d?.Files ?? new Dictionary<string, string>(), note, by, ct, resources: d?.ResourceFiles, plugins: plugins);
    }

    /// <summary>Installs a plugin copy (a new deployment).</summary>
    public async Task<Deployment> InstallPluginAsync(PluginRequest request, string? by, CancellationToken ct = default)
    {
        if (_current is null) await RefreshAsync(ct);
        if (Plugins.Any(p => p.Id == request.Id)) throw new FunctionDeployException($"A plugin is already installed as {request.Id}.", []);
        var install = await ResolvePluginAsync(request, "console", by, ct);
        return await WithPluginsAsync([.. Plugins, install], $"Installed {install.Id} ({install.Package} {install.Version})", by, ct);
    }

    /// <summary>Changes a plugin copy's settings (a new deployment).</summary>
    public async Task<Deployment> ConfigurePluginAsync(string id, IReadOnlyDictionary<string, JsonNode?> settings, string? by, CancellationToken ct = default)
    {
        var old = await InstalledAsync(id, ct);
        var settled = PluginSettings.Resolve(old.Manifest, settings, out var problems);
        if (problems.Count > 0) throw new FunctionDeployException($"{id}'s settings aren't right.", problems);
        var changed = old with { Settings = settled, Source = "console", By = by, At = DateTimeOffset.UtcNow };
        return await WithPluginsAsync([.. Plugins.Select(p => p.Id == id ? changed : p)], $"Changed {id}'s settings", by, ct);
    }

    /// <summary>
    /// Moves a plugin copy to <paramref name="version"/> (the newest when null), keeping its settings; settings the new
    /// version no longer has are dropped, and new ones it needs must be given in <paramref name="settings"/>.
    /// </summary>
    public async Task<Deployment> UpgradePluginAsync(string id, string? version, string? by, IReadOnlyDictionary<string, JsonNode?>? settings = null, CancellationToken ct = default)
    {
        var old = await InstalledAsync(id, ct);
        var package = await Packages.GetAsync(old.Package, version, ct);
        var kept = old.Settings.Where(s => package.Manifest.Settings.ContainsKey(s.Key)).ToDictionary(s => s.Key, s => s.Value);
        foreach (var (k, v) in settings ?? new Dictionary<string, JsonNode?>()) kept[k] = v;
        var settled = PluginSettings.Resolve(package.Manifest, kept, out var problems);
        if (problems.Count > 0) throw new FunctionDeployException($"{id} {package.Version} needs other settings.", problems);
        var moved = old with { Version = package.Version, Manifest = package.Manifest, Settings = settled, Source = "console", By = by, At = DateTimeOffset.UtcNow };
        return await WithPluginsAsync([.. Plugins.Select(p => p.Id == id ? moved : p)], $"{id}: {old.Package} {old.Version} → {package.Version}", by, ct);
    }

    /// <summary>
    /// Removes a plugin copy and its functions (a new deployment). What it wrote stays; its secrets go too when
    /// <paramref name="removeSecrets"/>.
    /// </summary>
    public async Task<Deployment> UninstallPluginAsync(string id, string? by, bool removeSecrets = false, CancellationToken ct = default)
    {
        var old = await InstalledAsync(id, ct);
        var d = await WithPluginsAsync([.. Plugins.Where(p => p.Id != id)], $"Removed {id} ({old.Package})", by, ct);
        if (removeSecrets)
            foreach (var name in old.SecretNames.Values) await secrets.RemoveAsync(name, ct);
        return d;
    }

    /// <summary>The installed plugins with their newest versions and the secrets each still needs.</summary>
    public async Task<IReadOnlyList<PluginInstallInfo>> PluginsAsync(bool checkUpdates = true, CancellationToken ct = default)
    {
        if (_current is null) await RefreshAsync(ct);
        var d = _current;
        if (d is null) return [];
        var set = (await secrets.ListAsync(d, ct)).Where(s => s.IsSet).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var list = new List<PluginInstallInfo>();
        foreach (var p in d.PluginList)
        {
            var latest = checkUpdates ? await Packages.LatestAsync(p.Package, ct) : null;
            var needed = p.Manifest.Secrets.Where(s => s.Value.Required && !set.Contains(p.SecretFor(s.Key))).Select(s => p.SecretFor(s.Key)).ToList();
            list.Add(new PluginInstallInfo(p, latest, needed, [.. d.Functions.Where(f => f.Plugin == p.Id).Select(f => f.Name)]));
        }
        return list;
    }

    private async Task<PluginInstall> InstalledAsync(string id, CancellationToken ct)
    {
        if (_current is null) await RefreshAsync(ct);
        return Plugins.FirstOrDefault(p => p.Id == id) ?? throw new FunctionDeployException($"There's no plugin installed as {id}.", []);
    }

    /// <summary>
    /// The plugins a deploy lists (winche.json): each resolved, the ones unchanged keeping where and when they were
    /// installed; the others are <c>deploy</c>'s.
    /// </summary>
    public async Task<IReadOnlyList<PluginInstall>> ResolveDeployPluginsAsync(IReadOnlyList<PluginRequest> requests, string? by, CancellationToken ct = default)
    {
        if (_current is null) await RefreshAsync(ct);
        var list = new List<PluginInstall>();
        var problems = new List<string>();
        foreach (var r in requests)
        {
            try
            {
                var p = await ResolvePluginAsync(r, "deploy", by, ct);
                list.Add(Plugins.FirstOrDefault(o => o.SameAs(p)) ?? p);
            }
            catch (FunctionDeployException ex) { problems.Add(ex.Problems.Count == 0 ? $"{r.Id}: {ex.Message}" : $"{r.Id}: {ex.Message} {string.Join(" ", ex.Problems)}"); }
        }
        if (problems.Count > 0) throw new FunctionDeployException("These plugins can't be installed.", problems);
        return list;
    }
}
