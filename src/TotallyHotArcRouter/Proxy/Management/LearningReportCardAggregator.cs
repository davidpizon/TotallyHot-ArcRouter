using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Turns the two stores the report card already has — usage rollups for spend, taxonomy comparisons
/// for grades and frozen-baseline score delta — into one <see cref="LearningReportCard"/>. Kept as a
/// class of static functions so the banding, the spend-fallback rule, and the delta formula live in
/// one place rather than as a pile of module-level helpers the GUI and the gRPC layer would each
/// reimplement.
/// </summary>
public static class LearningReportCardAggregator
{
    /// <summary>
    /// Letter grades in display order. Always emitted, even at count zero, so the mix's legend does not
    /// reshuffle when a bucket is empty.
    /// </summary>
    public static readonly string[] GradeOrder = ["A", "B", "C", "D", "F"];

    /// <summary>
    /// Maps a quality score in <c>[0, 1]</c> onto the 1–5 judge scale's letter grade. The bands sit on
    /// the midpoints between adjacent digits after the judge's <c>(digit − 1) / 4</c> normalisation, so
    /// a raw 5 lands in A, a 4 in B, a 3 in C, a 2 in D, and a 1 in F.
    /// </summary>
    /// <param name="score">The observed quality score; values outside <c>[0, 1]</c> still bucket at the ends.</param>
    /// <returns>One of <see cref="GradeOrder"/>.</returns>
    public static string GradeFromScore(double score)
    {
        return score switch
        {
            >= 0.875 => "A",
            >= 0.625 => "B",
            >= 0.375 => "C",
            >= 0.125 => "D",
            _ => "F"
        };
    }

    /// <summary>
    /// Builds the report card from already-loaded store rows. Spend prefers the usage-rollup feed
    /// (every priced request, not only scored ones); when that feed is empty the comparison rows'
    /// <see cref="TaxonomyComparisonRecord.ActualCostUsd"/> is the fallback so a learning-only database
    /// still shows spend rather than a blank panel.
    /// </summary>
    /// <param name="modelBuckets">Usage-rollup buckets grouped by model; empty when that store is unused or has no traffic.</param>
    /// <param name="comparisons">Taxonomy-comparison rows already clipped to the requested window.</param>
    public static LearningReportCard Aggregate(
        IReadOnlyList<UsageRollupBucket> modelBuckets,
        IReadOnlyList<TaxonomyComparisonRecord> comparisons)
    {
        ArgumentNullException.ThrowIfNull(modelBuckets);
        ArgumentNullException.ThrowIfNull(comparisons);

        var spend = modelBuckets.Count > 0
            ? SpendFromRollup(modelBuckets)
            : SpendFromComparisons(comparisons);
        var gradeMix = GradeMixFrom(comparisons);
        var (meanDelta, comparable, perModel) = ScoreDeltaFrom(comparisons);

        return new LearningReportCard(
            SpendByModel: spend,
            GradeMix: gradeMix,
            MeanScoreDelta: meanDelta,
            ScoredRequests: comparisons.Count,
            ComparableRequests: comparable,
            ScoreDeltaByModel: perModel,
            TotalSpendUsd: spend.Sum(row => row.CostUsd));
    }

    /// <summary>Sums rollup buckets that already group by model across the whole requested range.</summary>
    private static IReadOnlyList<ModelSpendRow> SpendFromRollup(IReadOnlyList<UsageRollupBucket> modelBuckets)
    {
        return
        [
            .. modelBuckets
                .GroupBy(bucket => bucket.GroupKey, StringComparer.Ordinal)
                .Select(group => new ModelSpendRow(
                    Model: group.Key,
                    CostUsd: group.Sum(bucket => bucket.CostUsd),
                    Requests: group.Sum(bucket => bucket.Requests)))
                .OrderByDescending(row => row.CostUsd)
                .ThenBy(row => row.Model, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Fallback spend when no usage rollup is present: priced comparison rows only, so an unpriced
    /// comparison is counted as a request but does not invent a dollar figure.
    /// </summary>
    private static IReadOnlyList<ModelSpendRow> SpendFromComparisons(
        IReadOnlyList<TaxonomyComparisonRecord> comparisons)
    {
        return
        [
            .. comparisons
                .GroupBy(row => row.RoutedModel, StringComparer.Ordinal)
                .Select(group => new ModelSpendRow(
                    Model: group.Key,
                    CostUsd: group.Sum(row => row.ActualCostUsd ?? 0m),
                    Requests: group.LongCount()))
                .OrderByDescending(row => row.CostUsd)
                .ThenBy(row => row.Model, StringComparer.Ordinal)
        ];
    }

    /// <summary>Buckets every comparison's observed score into the stable A–F mix.</summary>
    private static IReadOnlyList<GradeMixRow> GradeMixFrom(IReadOnlyList<TaxonomyComparisonRecord> comparisons)
    {
        var counts = GradeOrder.ToDictionary(grade => grade, _ => 0, StringComparer.Ordinal);
        foreach (var row in comparisons)
            counts[GradeFromScore(row.ObservedScore)]++;

        var total = comparisons.Count;
        return
        [
            .. GradeOrder.Select(grade => new GradeMixRow(
                Grade: grade,
                Count: counts[grade],
                Percent: total == 0 ? 0m : Math.Round(d: (decimal)counts[grade] / total * 100m, decimals: 1)))
        ];
    }

    /// <summary>
    /// Mean observed-minus-baseline score, overall and per routed model. Rows whose frozen baseline
    /// abstained (no predicted score) are excluded rather than drawn as a zero delta.
    /// </summary>
    private static (double? MeanDelta, int Comparable, IReadOnlyList<ModelScoreDeltaRow> PerModel)
        ScoreDeltaFrom(IReadOnlyList<TaxonomyComparisonRecord> comparisons)
    {
        var comparable = comparisons
            .Where(row => row.BaselinePredictedScore.HasValue)
            .Select(row => (row.RoutedModel, Delta: row.ObservedScore - row.BaselinePredictedScore!.Value))
            .ToList();

        if (comparable.Count == 0)
            return (null, 0, []);

        var perModel =
            comparable
                .GroupBy(row => row.RoutedModel, StringComparer.Ordinal)
                .Select(group => new ModelScoreDeltaRow(
                    Model: group.Key,
                    MeanDelta: group.Average(row => row.Delta),
                    SampleSize: group.Count()))
                .OrderByDescending(row => row.MeanDelta)
                .ThenBy(row => row.Model, StringComparer.Ordinal)
                .ToList();

        return (comparable.Average(row => row.Delta), comparable.Count, perModel);
    }
}
