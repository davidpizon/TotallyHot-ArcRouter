using System.Globalization;
using System.Text;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Builds N5's comparison report (research-doc Table 3): every baseline plus the Orchestrator arm,
/// replayed over the same split through the same <see cref="RegretReplayEngine"/> loop, so their
/// <c>CumReg</c>/<c>AvgPerf</c>/<c>TotTok</c>/<c>$Total</c>/<c>Perf/$</c> are directly comparable
/// (docs/router/regret-evaluation-harness-plan.md "The Orchestrator arm", N5's exit criterion).
/// </summary>
public static class RegretComparisonReportBuilder
{
    /// <summary>
    /// Replays every comparison baseline and the Orchestrator arm over <paramref name="outcomes"/>.
    /// <see cref="LogRegBaseline"/>/<see cref="KnnRetrievalBaseline"/> and the Orchestrator's <c>logreg</c>
    /// component are text/embedding-limited: on a split with neither (ID test, probing), they report a
    /// <see cref="RegretReplayResult.ScoredTaskCount"/> of zero rather than being omitted, so the report
    /// states explicitly - by showing zero routed tasks, not by silent absence - which rows were
    /// "not computable" on that split, per N5's exit criterion (docs/router/regret-evaluation-harness-plan.md).
    /// </summary>
    /// <param name="outcomes">The split to score every router against.</param>
    /// <param name="probingOutcomes">The probing split's outcomes, for warm-starting the bandit baselines.</param>
    /// <param name="probingMatrix">The frozen probing-split (dimension, model) prior, e.g. from <see cref="DimensionModelScoreMatrix.FromDatabase"/>.</param>
    /// <param name="logRegArtifact">The trained TF-IDF artifact backing <see cref="LogRegBaseline"/>, e.g. from <see cref="LogRegTrainer.Train"/>.</param>
    /// <param name="knnArtifact">The frozen embedding index backing <see cref="KnnRetrievalBaseline"/>, e.g. from <see cref="KnnRetrievalIndexBuilder.BuildAsync"/>.</param>
    /// <param name="orchestratorArm">The isolated Orchestrator arm, e.g. from <see cref="OrchestratorArmFactory.Build"/>.</param>
    /// <param name="weights">The reward weights - the same instance every router is scored under.</param>
    /// <returns>One <see cref="RegretReplayResult"/> per router, in a fixed, reproducible order (Always-*m* per model, DimensionBest, LinUCB, LinTS, LogReg, kNN Retrieval, Orchestrator).</returns>
    public static IReadOnlyList<RegretReplayResult> BuildReport(
        IReadOnlyList<RegretTaskOutcome> outcomes,
        IReadOnlyList<RegretTaskOutcome> probingOutcomes,
        DimensionModelScoreMatrix probingMatrix,
        LogRegModelArtifact logRegArtifact,
        KnnRetrievalArtifact knnArtifact,
        OrchestratorArmBaseline orchestratorArm,
        RewardWeights weights)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(probingOutcomes);
        ArgumentNullException.ThrowIfNull(probingMatrix);
        ArgumentNullException.ThrowIfNull(logRegArtifact);
        ArgumentNullException.ThrowIfNull(knnArtifact);
        ArgumentNullException.ThrowIfNull(orchestratorArm);
        ArgumentNullException.ThrowIfNull(weights);

        var results = new List<RegretReplayResult>();

        foreach (var modelId in DistinctModelIds(outcomes))
        {
            results.Add(Replay(new AlwaysModelBaseline(modelId), outcomes, weights));
        }

        results.Add(Replay(new DimensionBestBaseline(probingMatrix), outcomes, weights));

        var linUcb = new LinUcbBaseline();
        linUcb.WarmStart(probingOutcomes, weights);
        results.Add(Replay(linUcb, outcomes, weights));

        var linTs = new LinThompsonSamplingBaseline();
        linTs.WarmStart(probingOutcomes, weights);
        results.Add(Replay(linTs, outcomes, weights));

        results.Add(Replay(new LogRegBaseline(logRegArtifact), outcomes, weights));
        results.Add(Replay(new KnnRetrievalBaseline(knnArtifact), outcomes, weights));
        results.Add(Replay(orchestratorArm, outcomes, weights));

        return results;
    }

    /// <summary>Formats a report as a Markdown table, columns matching research-doc Table 3 plus scored/skipped task counts.</summary>
    /// <param name="title">The table's heading, e.g. the split name.</param>
    /// <param name="rows">The report rows, e.g. from <see cref="BuildReport"/>.</param>
    public static string FormatMarkdownTable(string title, IReadOnlyList<RegretReplayResult> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
        builder.AppendLine();
        builder.AppendLine("| Router | CumReg | AvgPerf | TotTok | $Total | Perf/$ | Scored | Skipped |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in rows)
        {
            var perfPerDollar = row.PerfPerDollar is { } value
                ? value.ToString("F2", CultureInfo.InvariantCulture)
                : "—";
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.RouterName} | {row.CumulativeRegret:F4} | {row.AvgPerf:F4} | {row.TotalTokens} | {row.TotalCostUsd:F4} | {perfPerDollar} | {row.ScoredTaskCount} | {row.SkippedTaskCount} |");
        }

        return builder.ToString();
    }

    /// <summary>Replays one router over <paramref name="outcomes"/> under <paramref name="weights"/>.</summary>
    private static RegretReplayResult Replay(IRegretBaselineRouter router, IReadOnlyList<RegretTaskOutcome> outcomes, RewardWeights weights) =>
        RegretReplayEngine.Replay(outcomes, router, weights);

    /// <summary>Every canonical model id scored on at least one task in <paramref name="outcomes"/>, in a fixed ordinal order.</summary>
    private static IReadOnlyList<string> DistinctModelIds(IReadOnlyList<RegretTaskOutcome> outcomes) =>
        [.. outcomes
            .SelectMany(outcome => outcome.Cells.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)];
}
