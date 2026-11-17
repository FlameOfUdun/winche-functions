namespace Winche.Functions;

/// <summary>
/// Who may run now: each function at most its <c>maxInstances</c> at once, and the project's runs split into each
/// function's reserved ones (<c>reservedInstances</c>, only for it) and a shared rest. A run takes its function's
/// reserved slot when one is free, else a shared one, waiting its turn (within its timeout).
/// </summary>
internal sealed class RunSlots(Func<int> total)
{
    private readonly Lock _lock = new();
    private string _shape = "";
    private IReadOnlyDictionary<string, int> _reservations = new Dictionary<string, int>();
    private SemaphoreSlim _shared = new(total(), total());
    private readonly Dictionary<string, (int Max, SemaphoreSlim Gate)> _max = [];
    private readonly Dictionary<string, (int Count, SemaphoreSlim Gate)> _reserved = [];

    /// <summary>
    /// The functions' reservations in force (by name). When they change, the shared rest is sized again; runs going on
    /// keep the slots they took.
    /// </summary>
    public void Reserve(IReadOnlyDictionary<string, int> reserved)
    {
        var all = total();
        var shape = all + ":" + string.Join(",", reserved.Where(r => r.Value > 0).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}={r.Value}"));
        lock (_lock)
        {
            if (shape == _shape) return;
            _shape = shape;
            _reservations = reserved;
            var rest = Math.Max(0, all - reserved.Values.Sum());
            _shared = new SemaphoreSlim(rest, Math.Max(rest, 1));
            _reserved.Clear();
            foreach (var (name, count) in reserved.Where(r => r.Value > 0)) _reserved[name] = (count, new SemaphoreSlim(count, count));
        }
    }

    /// <summary>A slot for a run of <paramref name="name"/> (at most <paramref name="max"/> at once), or null when none came within <paramref name="wait"/>.</summary>
    public async Task<IDisposable?> TakeAsync(string name, int max, TimeSpan wait, CancellationToken ct)
    {
        SemaphoreSlim own, shared;
        SemaphoreSlim? reserved;
        if (!_shape.StartsWith(total() + ":", StringComparison.Ordinal)) Reserve(_reservations);   // the server's limit changed
        lock (_lock)
        {
            if (!_max.TryGetValue(name, out var m) || m.Max != max) _max[name] = m = (max, new SemaphoreSlim(max, max));
            own = m.Gate;
            shared = _shared;
            reserved = _reserved.TryGetValue(name, out var r) ? r.Gate : null;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(wait);
        try
        {
            await own.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        try
        {
            if (reserved is not null && reserved.Wait(0)) return new Slot(own, reserved);
            await shared.WaitAsync(deadline.Token);
            return new Slot(own, shared);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { own.Release(); return null; }
        catch { own.Release(); throw; }
    }

    private sealed class Slot(SemaphoreSlim own, SemaphoreSlim pool) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            pool.Release();
            own.Release();
        }
    }
}
