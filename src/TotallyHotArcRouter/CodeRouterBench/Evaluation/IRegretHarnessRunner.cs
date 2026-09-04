namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Re-runs the N5 regret-evaluation comparison report on demand (docs/router/regret-evaluation-harness-plan.md
/// N6): the same recipe <c>N5ComparisonReportReconciliationTests</c> proves by hand, reachable from the
/// headless <c>--run-regret-harness</c> CLI flag and the Governance UI's Regret Harness panel. Read-only
/// and informational - unlike the <c>logreg</c>/cluster retrains, a run never mutates a live voter or
/// writes an artifact the router depends on.
/// </summary>
public interface IRegretHarnessRunner
{
    /// <summary>The most recently completed run's result, or <see langword="null"/> if none has run yet this process.</summary>
    /// <remarks>
    /// In-memory only - unlike the logreg/cluster artifacts, a run produces no file to recover status
    /// from after a process restart. Acceptable because this feature is diagnostic, not load-bearing
    /// state (docs/router/regret-evaluation-harness-plan.md's N6 status note).
    /// </remarks>
    RegretHarnessRunResult? LastResult { get; }

    /// <summary>
    /// Runs the full comparison report over the synced CodeRouterBench corpus: trains the standalone
    /// <c>logreg</c> baseline, embeds the OOD split via the real production embedding client to build the
    /// kNN index, builds the isolated Orchestrator arm, and replays every baseline plus the Orchestrator
    /// arm over both the ID-test and OOD splits.
    /// </summary>
    /// <param name="stageProgress">
    /// Reports each coarse stage as the run progresses through it, or <see langword="null"/> to run silently.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The run's outcome. <see cref="RegretHarnessRunResultKind.Declined"/> when the corpus is not ready
    /// (see <see cref="RegretHarnessRunResultKind.Declined"/>'s remarks); <see cref="RegretHarnessRunResultKind.AlreadyRunning"/>
    /// when another run is already in progress, in which case this call did not touch <see cref="LastResult"/>.
    /// </returns>
    Task<RegretHarnessRunResult> RunAsync(
        IProgress<RegretHarnessStage>? stageProgress = null,
        CancellationToken cancellationToken = default);
}
