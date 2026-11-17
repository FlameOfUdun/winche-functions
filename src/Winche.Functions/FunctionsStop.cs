using Microsoft.Extensions.Hosting;

namespace Winche.Functions;

/// <summary>
/// When the app stops (SIGTERM: a deploy, a scale-in), before any other service stops: functions take no new runs
/// (<c>unavailable</c>, which apps retry; triggered runs are delivered again) and the runs going on may end, within the
/// host's shutdown timeout.
/// </summary>
internal sealed class FunctionsStop(WincheFunctions functions) : IHostedLifecycleService
{
    public Task StoppingAsync(CancellationToken ct) => functions.StopTakingRunsAsync(ct);
    public Task StartingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}
