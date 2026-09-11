namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Fallback <see cref="IJudgeCalibrationAnalyzer"/> used only when <see cref="Proxy.ProxyServer"/> is
/// constructed without a <see cref="Proxy.JudgeCalibrationAdminDependencies"/> group (e.g. a minimal test
/// harness that doesn't care about this feature). <see cref="JudgeCalibrationAdminGrpcService"/> is mapped
/// unconditionally - see <see cref="Proxy.ProxyServerDependencies.JudgeCalibrationAdmin"/>'s remarks - so
/// it must always have something constructible to resolve. Mirrors
/// <see cref="CodeRouterBench.Evaluation.NullRegretHarnessRunner"/>'s null-object convention.
/// </summary>
public sealed class NullJudgeCalibrationAnalyzer : IJudgeCalibrationAnalyzer
{
    /// <inheritdoc/>
    public int MinimumSampleSize => 0;

    /// <summary>
    /// Returns an empty report carrying a single <see cref="JudgeCalibrationVerdictKind.Unevaluable"/>
    /// verdict that names the reason.
    /// </summary>
    /// <remarks>
    /// Deliberately not a silently empty report. An empty one is indistinguishable from "the judge has
    /// simply not graded anything yet", which would let a misconfigured server look like a merely idle
    /// one - the panel would show a clean, reassuring "no rows" state for a service that is not wired up
    /// at all. The verdict says which of the two it is.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An empty report explaining that no analyzer is configured.</returns>
    public Task<JudgeCalibrationReport> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new JudgeCalibrationReport(
            Cohorts: [],
            SelfPreference: [],
            Verdicts:
            [
                new JudgeCalibrationVerdict(
                    Condition: "analyzer-configured",
                    Kind: JudgeCalibrationVerdictKind.Unevaluable,
                    Detail: "Judge calibration analysis was not configured for this server instance. "
                            + "No shadow rows were read, so an empty report here means nothing about the judge.")
            ],
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            TotalRowsAnalyzed: 0));
    }
}
