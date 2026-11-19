namespace Winche.Functions;

public sealed class FunctionsOptions
{
    /// <summary>The project's database: deployments and schedules live there.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The deno program; found on PATH when not set (Winche Server's image has it).</summary>
    public string? DenoPath { get; set; }

    /// <summary>Deno's download cache for npm: and jsr: imports (default: a folder under the temp folder).</summary>
    public string? CachePath { get; set; }

    /// <summary>
    /// Packages this server provides to deployments that import them without saying where from (deno.json doesn't name
    /// them): package name (e.g. <c>@winche-technologies/winche-admin</c>) → a folder with its package.json and built files. For servers
    /// that can't reach npm, and tests. Empty by default: a deployment names its packages itself.
    /// </summary>
    public Dictionary<string, string> ProvidedPackages { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Plugin packages this server provides from folders (package name → a folder with its package.json,
    /// winche-plugin.json and built files), over npm: for servers that can't reach npm, and tests.
    /// </summary>
    public Dictionary<string, string> ProvidedPlugins { get; } = new(StringComparer.Ordinal);

    /// <summary>The npm registry plugin packages come from (default https://registry.npmjs.org/).</summary>
    public string PluginRegistry { get; set; } = "https://registry.npmjs.org/";

    /// <summary>How many functions may run at once on this server (default 16); more wait their turn.</summary>
    public int MaxConcurrency { get; set; } = 16;

    /// <summary>The most memory a function may ask for, in MB (default 1024): what its code and settings can go up to.</summary>
    public int MaxMemoryMb { get; set; } = 1024;

    /// <summary>A function's memory unless it sets its own, in MB (default 256).</summary>
    public int DefaultMemoryMb { get; set; } = 256;

    /// <summary>The most runs of one function at once unless it sets its own (default 10).</summary>
    public int DefaultMaxInstances { get; set; } = 10;

    /// <summary>
    /// The heap limit of each worker in the shared process, in MB (default 1024). A run past it stops that process (its
    /// runs in flight fail as out of memory) and it starts again; a function asking for more memory runs in its own.
    /// </summary>
    public int SharedProcessMemoryMb { get; set; } = 1024;

    /// <summary>The most Deno processes per project (default 4): the shared one and isolated functions' own.</summary>
    public int MaxProcesses { get; set; } = 4;

    /// <summary>
    /// What functions' processes may hold in all, shared by every project of the server (default: 60% of the
    /// container's memory, or of the machine's; <c>WINCHE_FUNCTIONS_MEMORY_MB</c> sets it outright). An isolated
    /// process that wouldn't fit isn't started: its function runs in the shared process.
    /// </summary>
    public FunctionsMemoryBudget? MemoryBudget { get; set; }

    /// <summary>A function's time unless it sets its own (default 60 s).</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The longest any function may run (default 9 minutes): what code, the default and each function's own time can
    /// go up to. A run that long still ends if the server restarts: long work saves its progress as it goes.
    /// </summary>
    public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromMinutes(9);

    /// <summary>Lets the console change the default run time and each function's own (settings area <c>functions</c>).</summary>
    public FunctionsOptions UseSettings()
    {
        Settings = true;
        return this;
    }

    internal bool Settings { get; private set; }

    /// <summary>
    /// Who called a callable, when functions should read it its own way; by default it is the request's caller
    /// (Winche.Core's <c>AddWincheCaller</c>). Null claims mean signed out.
    /// Needed for <c>ctx.auth</c>.
    /// </summary>
    public Func<Microsoft.AspNetCore.Http.HttpContext, IReadOnlyDictionary<string, object?>?>? Claims { get; set; }

    /// <summary>The largest request body an HTTP function takes (default 1 MB); larger ones are refused with 413.</summary>
    public long MaxHttpRequestBytes { get; set; } = 1L << 20;

    /// <summary>How often each server looks for a new deployment made on another server (default 3 s).</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(3);
}
