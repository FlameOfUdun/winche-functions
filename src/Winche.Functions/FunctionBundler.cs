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
            await File.WriteAllTextAsync(Path.Combine(dir, ".winche", "functions.ts"), DenoRuntime.Resource("functions.ts"), ct);
            // Their deno.json (import map, npm versions) with @winche/functions pointed at the SDK.
            var config = files.TryGetValue("deno.json", out var json) ? JsonNode.Parse(json, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? [] : [];
            if (config["imports"] is not JsonObject imports) config["imports"] = imports = [];
            imports["@winche/functions"] = "./.winche/functions.ts";
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
}
