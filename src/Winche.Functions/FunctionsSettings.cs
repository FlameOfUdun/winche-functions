namespace Winche.Functions;

/// <summary>
/// The settings area <c>functions</c> (with <see cref="FunctionsOptions.UseSettings"/>): how long functions may run,
/// changed in the console (Functions → Settings) and applied from the next run.
/// </summary>
public sealed record FunctionsSettings
{
    public const string Area = "functions";

    /// <summary>A function's time when neither its own setting nor its code says (default 60 s).</summary>
    public int DefaultTimeoutSeconds { get; init; } = 60;

    /// <summary>Times for single functions, by name, over what their code says.</summary>
    public IReadOnlyDictionary<string, int> Timeouts { get; init; } = new Dictionary<string, int>();

    /// <summary>A function's memory, in MB, when neither its own setting nor its code says.</summary>
    public int DefaultMemoryMb { get; init; } = 256;

    /// <summary>The most runs of one function at once when neither its own setting nor its code says.</summary>
    public int DefaultMaxInstances { get; init; } = 10;

    /// <summary>Limits for single functions, by name, over what their code says (only what's set).</summary>
    public IReadOnlyDictionary<string, FunctionLimits> Limits { get; init; } = new Dictionary<string, FunctionLimits>();

    /// <summary>What's wrong, in sentences: every time is between 1 second and <paramref name="maxSeconds"/>.</summary>
    public IReadOnlyList<string> Problems(int maxSeconds) => Problems(maxSeconds, 1024, 16);

    /// <summary>What's wrong, in sentences: times, memory and instances within this server's limits.</summary>
    public IReadOnlyList<string> Problems(int maxSeconds, int maxMemoryMb, int maxConcurrency) => Problems(maxSeconds, maxMemoryMb, maxConcurrency, null, 0);

    /// <summary>
    /// As above, and a function in a process of its own (isolated, or asking for more than the shared process's
    /// <paramref name="sharedMb"/>) must fit <paramref name="budget"/> beside the shared process.
    /// </summary>
    public IReadOnlyList<string> Problems(int maxSeconds, int maxMemoryMb, int maxConcurrency, FunctionsMemoryBudget? budget, int sharedMb)
    {
        var problems = new List<string>();
        if (budget is not null)
            foreach (var (name, l) in Limits)
                if (l.MemoryMb is { } m && (l.Isolate == true || m > sharedMb) && m + FunctionLimitRules.ProcessRoomMb + sharedMb > budget.TotalMb)
                    problems.Add($"{name}: {m} MB in a process of its own doesn't fit this server's functions memory ({budget.TotalMb} MB, " +
                        $"{sharedMb} MB of it for the shared process): at most {Math.Max(0, budget.TotalMb - sharedMb - FunctionLimitRules.ProcessRoomMb)} MB.");
        // The project's defaults may be above this server's limits (a smaller server, or its limits lowered): they're
        // held to them where they apply, not refused (that would refuse every other change too).
        if (DefaultMemoryMb < FunctionLimitRules.MinMemoryMb) problems.Add($"The default memory is at least {FunctionLimitRules.MinMemoryMb} MB.");
        if (DefaultMaxInstances < 1) problems.Add("The default most runs at once is at least 1.");
        foreach (var (name, limits) in Limits)
            problems.AddRange(FunctionLimitRules.Problems(string.IsNullOrWhiteSpace(name) ? "A function" : name, limits, maxMemoryMb, maxConcurrency));
        var reserved = Limits.Values.Sum(l => l.ReservedInstances ?? 0);
        if (reserved > maxConcurrency) problems.Add($"Functions reserve {reserved} runs at once in all; this server allows {maxConcurrency}.");
        string Most() => maxSeconds % 60 == 0 ? $"{maxSeconds / 60} minutes" : $"{maxSeconds} seconds";
        if (DefaultTimeoutSeconds < 1 || DefaultTimeoutSeconds > maxSeconds) problems.Add($"The default time is 1 second to {Most()} (this server's longest).");
        foreach (var (name, seconds) in Timeouts)
        {
            if (string.IsNullOrWhiteSpace(name)) problems.Add("A function's time needs its name.");
            else if (seconds < 1 || seconds > maxSeconds) problems.Add($"{name}: 1 second to {Most()} (this server's longest).");
        }
        return problems;
    }
}

/// <summary>The bounds a function's limits are checked against (in code when deploying, in the settings when saving).</summary>
public static class FunctionLimitRules
{
    /// <summary>The least memory a function may ask for, in MB.</summary>
    public const int MinMemoryMb = 64;

    /// <summary>What an isolated process holds beside its function's memory: garbage between collections.</summary>
    public const int ProcessRoomMb = 128;

    public static IReadOnlyList<string> Problems(string name, FunctionLimits l, int maxMemoryMb, int maxConcurrency)
    {
        var problems = new List<string>();
        if (l.MemoryMb is { } m && (m < MinMemoryMb || m > maxMemoryMb)) problems.Add($"{name}: memory is {MinMemoryMb} to {maxMemoryMb} MB (this server's most).");
        if (l.MaxInstances is { } max && (max < 1 || max > maxConcurrency)) problems.Add($"{name}: maxInstances is 1 to {maxConcurrency} (this server's most).");
        if (l.MinInstances is { } min && (min < 0 || min > (l.MaxInstances ?? maxConcurrency))) problems.Add($"{name}: minInstances is 0 to its maxInstances.");
        if (l.ReservedInstances is { } r && (r < 0 || r > (l.MaxInstances ?? maxConcurrency))) problems.Add($"{name}: reservedInstances is 0 to its maxInstances.");
        return problems;
    }
}
