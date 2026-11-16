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

    /// <summary>What's wrong, in sentences: every time is between 1 second and <paramref name="maxSeconds"/>.</summary>
    public IReadOnlyList<string> Problems(int maxSeconds)
    {
        var problems = new List<string>();
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
