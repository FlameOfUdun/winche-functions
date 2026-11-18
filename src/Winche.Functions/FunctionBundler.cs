using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Winche.Functions;

/// <summary>Turns a deployment's files into one JavaScript bundle with <c>deno bundle</c> (imports resolved, TypeScript removed).</summary>
public sealed class FunctionBundler(FunctionsOptions options)
{
    public static readonly string[] Entries = ["index.ts", "index.js", "main.ts", "main.js"];
    private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*m");

    /// <summary>What's wrong with the files themselves (names, entry point).</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyDictionary<string, string> files)
    {
        var problems = new List<string>();
        if (files.Count == 0) problems.Add("There are no files.");
        if (!files.Keys.Any(f => Entries.Contains(f))) problems.Add($"Add an entry file: {string.Join(", ", Entries)}.");
        foreach (var f in files.Keys)
            if (f.Length > 200 || f.StartsWith('/') || f.Contains('\\') || f.Split('/').Any(p => p is "" or "." or ".." ) || f.StartsWith(".winche/", StringComparison.Ordinal))
                problems.Add($"'{f}' isn't a usable file name (relative, with / between folders).");
        if (files.Values.Sum(v => (long)v.Length) > 5_000_000) problems.Add("The files come to more than 5 MB.");
        return problems;
    }

    private static readonly Regex WincheImport = new(@"(?:from|import)\s*\(?\s*[""'](@winche-technologies/winche-(?:functions|admin))(?:/[^""']*)?[""']");

    /// <summary>
    /// Packages the code imports by name that its deno.json doesn't map and the server doesn't provide: what to add, in a
    /// sentence each.
    /// </summary>
    public IReadOnlyList<string> MissingPackages(IReadOnlyDictionary<string, string> files)
    {
        var imports = ImportsOf(files);
        var used = files.Where(f => f.Key.EndsWith(".ts") || f.Key.EndsWith(".js") || f.Key.EndsWith(".mjs") || f.Key.EndsWith(".tsx"))
            .SelectMany(f => WincheImport.Matches(f.Value).Select(m => m.Groups[1].Value)).ToHashSet();
        return [.. used.Where(name => !imports.ContainsKey(name) && !options.ProvidedPackages.ContainsKey(name)).Order()
            .Select(name => $"The code imports {name}, which deno.json doesn't name: add \"{name}\": \"{FunctionsSdk.Packages.First(p => p.Name == name).Specifier}\" to its imports.")];
    }

    private static JsonObject ImportsOf(IReadOnlyDictionary<string, string> files) =>
        (files.TryGetValue("deno.json", out var json) ? JsonNode.Parse(json, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject : null)?["imports"] as JsonObject ?? [];

    public async Task<string> BundleAsync(IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        if (Problems(files) is { Count: > 0 } problems) throw new FunctionDeployException("These files can't be deployed.", problems);
        var dir = Path.Combine(DenoRuntime.WorkFolder("builds"), Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (name, text) in files)
            {
                var path = Path.Combine(dir, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, text, ct);
            }
            Directory.CreateDirectory(Path.Combine(dir, ".winche"));
            // Their deno.json (import map, npm versions), with the packages the server provides for names it doesn't map.
            var config = files.TryGetValue("deno.json", out var json) ? JsonNode.Parse(json, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? [] : [];
            if (config["imports"] is not JsonObject imports) config["imports"] = imports = [];
            foreach (var (name, folder) in options.ProvidedPackages)
            {
                if (imports.ContainsKey(name)) continue;
                var target = Path.Combine(dir, ".winche", "packages", name);
                CopyFolder(folder, target);
                foreach (var (subpath, file) in Exports(folder))
                    if (!imports.ContainsKey(name + subpath)) imports[name + subpath] = $"./.winche/packages/{name}/{file}";
            }
            await File.WriteAllTextAsync(Path.Combine(dir, "deno.json"), config.ToJsonString(), ct);

            var entry = Entries.First(files.ContainsKey);
            var info = new ProcessStartInfo(DenoRuntime.Locate(options)) { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "bundle", "--quiet", "--config", "deno.json", entry, "-o", ".winche/bundle.js" }) info.ArgumentList.Add(a);
            info.Environment["NO_COLOR"] = "1";
            if (options.CachePath is { } cache) info.Environment["DENO_DIR"] = cache;
            using var process = System.Diagnostics.Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(true);
                throw new FunctionDeployException("Bundling took more than 2 minutes.", ["Check the imports: a download may be stuck."]);
            }
            if (process.ExitCode != 0)
            {
                var text = Ansi.Replace(await errors, "").Replace(dir + Path.DirectorySeparatorChar, "").Replace("file://" + dir + "/", "").Trim();
                throw new FunctionDeployException("The code doesn't build.", [text.Length > 4000 ? text[..4000] : text]);
            }
            return await File.ReadAllTextAsync(Path.Combine(dir, ".winche", "bundle.js"), ct);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A package's entry points from its package.json exports: subpath ("" or "/testing") → file, relative to it.</summary>
    private static IEnumerable<(string Subpath, string File)> Exports(string folder)
    {
        var pkg = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "package.json"))) as JsonObject ?? [];
        static string? Target(JsonNode? n) => n switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonObject o => Target(o["import"]) ?? Target(o["default"]),
            _ => null,
        };
        if (pkg["exports"] is JsonObject exports && exports.Any(e => e.Key.StartsWith('.')))
        {
            foreach (var (key, value) in exports)
                if (Target(value) is { } file) yield return (key == "." ? "" : key[1..], file.TrimStart('.', '/'));
        }
        else if ((Target(pkg["exports"]) ?? pkg["module"]?.GetValue<string>() ?? pkg["main"]?.GetValue<string>()) is { } main)
            yield return ("", main.TrimStart('.', '/'));
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            if (rel.Split(Path.DirectorySeparatorChar).Contains("node_modules")) continue;
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
