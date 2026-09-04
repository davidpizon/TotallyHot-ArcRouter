namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Fallback <see cref="IRegretHarnessRunner"/> used only when <see cref="Proxy.ProxyServer"/> is
/// constructed without a <see cref="Proxy.RegretHarnessAdminDependencies"/> group (e.g. a minimal test
/// harness that doesn't care about this feature). <see cref="RegretHarnessAdminGrpcService"/> is mapped
/// unconditionally - see <see cref="Proxy.ProxyServerDependencies.RegretHarnessAdmin"/>'s remarks - so it
/// must always have something constructible to resolve, even when nothing real backs it. Mirrors
/// <see cref="Update.NullReleaseCheckClient"/>'s null-object convention.
/// </summary>
public sealed class NullRegretHarnessRunner : IRegretHarnessRunner
{
    /// <inheritdoc/>
    public RegretHarnessRunResult? LastResult => null;

    /// <inheritdoc/>
    public Task<RegretHarnessRunResult> RunAsync(
        IProgress<RegretHarnessStage>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RegretHarnessRunResult(
            RegretHarnessRunResultKind.Declined,
            Message: "The regret evaluation harness was not configured for this server instance.",
            null,
            Splits: []));
    }
}
