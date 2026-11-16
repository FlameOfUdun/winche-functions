namespace Winche.Functions;

public sealed class FunctionsOptions
{
    /// <summary>The project's database: deployments and schedules live there.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The deno program; found on PATH when not set (Winche Server's image has it).</summary>
    public string? DenoPath { get; set; }

    /// <summary>Deno's download cache for npm: and jsr: imports (default: a folder under the temp folder).</summary>
    public string? CachePath { get; set; }

    /// <summary>How many functions may run at once on this server (default 16); more wait their turn.</summary>
    public int MaxConcurrency { get; set; } = 16;

    /// <summary>A function's time unless it sets its own (default 60 s).</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The longest a function may set (default 9 minutes).</summary>
    public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromMinutes(9);

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
