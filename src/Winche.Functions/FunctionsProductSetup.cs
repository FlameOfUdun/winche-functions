using Winche.Core;

namespace Winche.Functions;

/// <summary>
/// Functions' setup for hosts that set products up on demand: where deployments are kept. (Without Deno, functions still
/// start: the console says Deno is missing where functions are deployed.)
/// </summary>
internal sealed class FunctionsProductSetup(FunctionStore store) : IWincheProductSetup
{
    public string Product => WincheProductNames.Functions;

    public IReadOnlyList<string> Steps { get; } = ["Create the function store"];

    public Task RunStepAsync(int step, CancellationToken ct) =>
        step == 0 ? store.InitializeAsync(ct) : throw new ArgumentOutOfRangeException(nameof(step));
}
