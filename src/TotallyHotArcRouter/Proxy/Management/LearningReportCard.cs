namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// One model's spend in the unified learning report card: dollars and request count already recorded
/// by the usage-rollup store (or, when that store is empty, by scored taxonomy comparisons), never a
/// second metrics pipeline.
/// </summary>
/// <param name="Model">The routed model this row aggregates.</param>
/// <param name="CostUsd">Known USD cost for <paramref name="Model"/> in the requested window.</param>
/// <param name="Requests">How many requests contributed to this row.</param>
public sealed record ModelSpendRow(string Model, decimal CostUsd, long Requests);

/// <summary>
/// One letter-grade bucket of observed quality scores. The five grades A–F always appear so a missing
/// bucket reads as "none scored here" rather than disappearing from the mix.
/// </summary>
/// <param name="Grade">The letter grade (<c>A</c> through <c>F</c>).</param>
/// <param name="Count">How many scored comparisons landed in this bucket.</param>
/// <param name="Percent">
/// <paramref name="Count"/> as a percentage of every scored comparison in the window, or zero when
/// nothing has been scored yet.
/// </param>
public sealed record GradeMixRow(string Grade, int Count, decimal Percent);

/// <summary>
/// One model's mean quality-score delta versus the frozen untrained baseline, for the report card's
/// score-delta panel.
/// </summary>
/// <param name="Model">The model that actually served the compared requests.</param>
/// <param name="MeanDelta">
/// Mean of <c>observed score − baseline predicted score</c> for comparable rows. Positive means the
/// routed answers scored better than the frozen policy was predicted to.
/// </param>
/// <param name="SampleSize">How many comparisons contributed to <paramref name="MeanDelta"/>.</param>
public sealed record ModelScoreDeltaRow(string Model, double MeanDelta, int SampleSize);

/// <summary>
/// The unified spend / grade-mix / score-delta snapshot the dashboard's Report Card tab renders. Built
/// from the existing usage-rollup store and the taxonomy-comparison (learning) store — not a parallel
/// metrics stack.
/// </summary>
/// <param name="SpendByModel">Per-model spend, cost-descending.</param>
/// <param name="GradeMix">Observed-score letter-grade distribution, always A–F in that order.</param>
/// <param name="MeanScoreDelta">
/// Unweighted mean of per-request score deltas versus the frozen baseline, or <see langword="null"/>
/// when no comparable row exists in the window.
/// </param>
/// <param name="ScoredRequests">How many comparison rows carried an observed quality score.</param>
/// <param name="ComparableRequests">
/// How many comparison rows carried both an observed score and a frozen-baseline predicted score, so a
/// delta could be computed rather than fabricated.
/// </param>
/// <param name="ScoreDeltaByModel">Per-model mean score delta, largest win first.</param>
/// <param name="TotalSpendUsd">Sum of <paramref name="SpendByModel"/> costs.</param>
public sealed record LearningReportCard(
    IReadOnlyList<ModelSpendRow> SpendByModel,
    IReadOnlyList<GradeMixRow> GradeMix,
    double? MeanScoreDelta,
    int ScoredRequests,
    int ComparableRequests,
    IReadOnlyList<ModelScoreDeltaRow> ScoreDeltaByModel,
    decimal TotalSpendUsd);
