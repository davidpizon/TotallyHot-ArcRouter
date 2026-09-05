namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The result category of one <see cref="IRegretHarnessRunner.RunAsync"/> call.
/// </summary>
public enum RegretHarnessRunResultKind
{
    /// <summary>The full comparison report was built for both splits.</summary>
    Completed,

    /// <summary>
    /// The synced CodeRouterBench corpus lacks at least one resolved OOD result or at least one
    /// <c>id_test</c> row - the same precondition <c>N5ComparisonReportReconciliationTests</c> checks
    /// before replaying.
    /// </summary>
    Declined,

    /// <summary>A run was already in progress; this call was skipped rather than queued.</summary>
    AlreadyRunning
}

/// <summary>
/// A coarse-grained stage of one harness run, reported via <see cref="IRegretHarnessRunner.RunAsync"/>'s
/// progress callback. Deliberately coarser than the logreg/cluster retrains' per-task
/// bootstrap-embedding tick: <see cref="KnnRetrievalIndexBuilder.BuildAsync"/>, the one step here that
/// embeds many items, has no <see cref="IProgress{T}"/> hook of its own, and adding one to an already-tested
/// baseline builder for the sake of this diagnostic-only feature was judged not worth the churn - see
/// docs/router/regret-evaluation-harness-plan.md's N6 status note.
/// </summary>
public enum RegretHarnessStage
{
    /// <summary>Reading the probing/OOD/ID-test outcome rows and the probing-split prior from the corpus.</summary>
    LoadingCorpus,

    /// <summary>Training the standalone TF-IDF <c>logreg</c> baseline.</summary>
    TrainingLogReg,

    /// <summary>Embedding the OOD split to build the kNN retrieval index - the one real embedding-client call.</summary>
    BuildingKnnIndex,

    /// <summary>Building the isolated, two-voter Orchestrator arm.</summary>
    BuildingOrchestratorArm,

    /// <summary>Replaying every baseline and the Orchestrator arm over both splits and formatting the report.</summary>
    BuildingReports
}

/// <summary>One split's formatted comparison report.</summary>
/// <param name="SplitName">The split's name (e.g. <c>"ID test"</c> or <c>"OOD"</c>).</param>
/// <param name="MarkdownTable">
/// The split's report, formatted by <see cref="RegretComparisonReportBuilder.FormatMarkdownTable"/> - the
/// same "publish these numbers" convention the N5 reconciliation test's changelog entries already use.
/// </param>
public sealed record RegretHarnessSplitReport(string SplitName, string MarkdownTable);

/// <summary>
/// The outcome of one harness run: which <see cref="RegretHarnessRunResultKind"/> category it fell into,
/// a human-readable message, when it ran, and - only on <see cref="RegretHarnessRunResultKind.Completed"/> -
/// the formatted report for each split.
/// </summary>
/// <param name="Kind">The result category.</param>
/// <param name="Message">A human-readable explanation, suitable for a status line.</param>
/// <param name="RanAtUtc">When this run completed, or <see langword="null"/> if no run has ever completed.</param>
/// <param name="Splits">The formatted report for each split, empty unless <paramref name="Kind"/> is <see cref="RegretHarnessRunResultKind.Completed"/>.</param>
public sealed record RegretHarnessRunResult(
    RegretHarnessRunResultKind Kind,
    string Message,
    DateTimeOffset? RanAtUtc,
    IReadOnlyList<RegretHarnessSplitReport> Splits);
