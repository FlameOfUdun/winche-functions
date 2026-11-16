using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Winche.Core;

namespace Winche.Functions;

/// <summary>A function secret as the console shows it: whether it's set, when and by whom, and which functions declare it. Never its value.</summary>
public sealed record FunctionSecretInfo(string Name, bool IsSet, DateTimeOffset? SetAt, string? SetBy, IReadOnlyList<string> UsedBy);

/// <summary>
/// Secrets functions read with <c>ctx.secrets.get</c> (API keys, passwords): kept in Winche.Core's <see cref="SecretStore"/>,
/// encrypted with the master key, as <c>functions.secret.NAME</c>. A function only gets the ones it declares
/// (<c>{ secrets: ["NAME"] }</c>); the console and CLI set and remove them but never read them back.
/// </summary>
public sealed class FunctionSecrets(IServiceProvider services)
{
    public const string Prefix = "functions.secret.";
    private static readonly Regex NameRule = new("^[A-Z][A-Z0-9_]{0,63}$");

    private SecretStore? Store => services.GetService<SecretStore>();

    /// <summary>Whether secrets can be kept here (Winche.Core's AddWincheConfig with a master key).</summary>
    public bool Available => Store is { Available: true };

    internal const string Unavailable = "Function secrets need Winche.Core's AddWincheConfig with a master key.";

    /// <summary>Why <paramref name="name"/> can't be a secret's name, or null.</summary>
    public static string? Problem(string? name) => name is not null && NameRule.IsMatch(name) ? null
        : $"'{name}' isn't a secret name: capitals, digits and underscores, starting with a letter (e.g. SES_SECRET_KEY).";

    /// <summary>The saved secrets and those the deployment in force declares, by name.</summary>
    public async Task<IReadOnlyList<FunctionSecretInfo>> ListAsync(Deployment? current, CancellationToken ct = default)
    {
        var saved = Store is { } store ? await store.ListAsync(Prefix, ct) : [];
        var declared = (current?.Functions ?? []).SelectMany(f => (f.Secrets ?? []).Select(s => (Secret: s, f.Name))).ToLookup(x => x.Secret, x => x.Name);
        var names = saved.Select(s => s.Name[Prefix.Length..]).Concat(declared.Select(g => g.Key)).Distinct().Order(StringComparer.Ordinal);
        return [.. names.Select(n =>
        {
            var info = saved.FirstOrDefault(s => s.Name == Prefix + n);
            return new FunctionSecretInfo(n, info is not null, info?.SetAt, info?.SetBy, [.. declared[n].Order(StringComparer.Ordinal)]);
        })];
    }

    public async Task SetAsync(string name, string value, string? by, CancellationToken ct = default)
    {
        if (Problem(name) is { } problem) throw new ArgumentException(problem, nameof(name));
        if (Store is not { Available: true } store) throw new InvalidOperationException(Unavailable);
        await store.SetAsync(Prefix + name, value, by, ct);
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        if (Problem(name) is { } problem) throw new ArgumentException(problem, nameof(name));
        return Store is { } store && await store.DeleteAsync(Prefix + name, ct);
    }

    /// <summary>The declared secrets' values for one run (null for those not set).</summary>
    internal async Task<Dictionary<string, string?>> ForRunAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>();
        foreach (var n in names) values[n] = Store is { Available: true } store ? await store.GetAsync(Prefix + n, ct) : null;
        return values;
    }

    /// <summary><paramref name="text"/> with every secret value of the run replaced by ••••.</summary>
    internal static string Hide(string text, IEnumerable<string> values)
    {
        foreach (var v in values.OrderByDescending(v => v.Length))
            if (v.Length >= 4) text = text.Replace(v, "••••", StringComparison.Ordinal);
        return text;
    }
}
