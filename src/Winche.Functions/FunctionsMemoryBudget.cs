namespace Winche.Functions;

/// <summary>
/// What functions' Deno processes may hold in all, in MB: each process's heap limit is taken from it when it starts
/// and given back when it stops. Isolated processes are refused when they wouldn't fit; the shared one always starts.
/// One budget is shared by every project of a server.
/// </summary>
public sealed class FunctionsMemoryBudget(int totalMb, string source = "set")
{
    private readonly Lock _lock = new();
    private int _used;

    public int TotalMb => totalMb;
    public int UsedMb { get { lock (_lock) return _used; } }

    /// <summary>Where the total came from ("container", "machine", "set"), for messages.</summary>
    public string Source => source;

    /// <summary>Takes <paramref name="mb"/> if it fits.</summary>
    public bool TryReserve(int mb)
    {
        lock (_lock)
        {
            if (_used + mb > totalMb) return false;
            _used += mb;
            return true;
        }
    }

    /// <summary>Takes <paramref name="mb"/> whether or not it fits (the shared process); returns it.</summary>
    public int Take(int mb)
    {
        lock (_lock) _used += mb;
        return mb;
    }

    public void Release(int mb)
    {
        lock (_lock) _used = Math.Max(0, _used - mb);
    }

    internal string Describe() => $"{UsedMb} of {TotalMb} MB in use, " + (Source == "set" ? "set by WINCHE_FUNCTIONS_MEMORY_MB" : $"60% of the {Source}'s memory");

    private static readonly Lazy<FunctionsMemoryBudget> Container = new(() =>
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("WINCHE_FUNCTIONS_MEMORY_MB"), out var set) && set > 0)
            return new FunctionsMemoryBudget(set, "set");
        // .NET reads the container's limit (cgroup memory.max) when there is one, else the machine's memory.
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var limited = File.Exists("/sys/fs/cgroup/memory.max") && File.ReadAllText("/sys/fs/cgroup/memory.max").Trim() is var max && max != "max";
        return new FunctionsMemoryBudget((int)(available / (1024 * 1024) * 6 / 10), limited ? "container" : "machine");
    });

    /// <summary>The server's budget: 60% of its container's memory (or the machine's), shared by every project.</summary>
    public static FunctionsMemoryBudget FromContainer() => Container.Value;
}
