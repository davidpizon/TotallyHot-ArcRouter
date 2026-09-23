using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="LearningReportCardAggregator"/>: letter-grade banding, spend preferring the
/// usage-rollup feed, comparison-cost fallback, and score-delta skipping rows without a frozen-baseline
/// prediction.
/// </summary>
public sealed class LearningReportCardAggregatorTests
{
    [Theory]
    [InlineData(1.0, "A")]
    [InlineData(0.875, "A")]
    [InlineData(0.874, "B")]
    [InlineData(0.80, "B")]
    [InlineData(0.75, "B")]
    [InlineData(0.625, "B")]
    [InlineData(0.624, "C")]
    [InlineData(0.5, "C")]
    [InlineData(0.375, "C")]
    [InlineData(0.374, "D")]
    [InlineData(0.25, "D")]
    [InlineData(0.125, "D")]
    [InlineData(0.124, "F")]
    [InlineData(0.0, "F")]
    public void GradeFromScore_UsesJudgeScaleMidpoints(double score, string expected)
    {
        Assert.Equal(expected, actual: LearningReportCardAggregator.GradeFromScore(score));
    }

    [Fact]
    public void Aggregate_PrefersRollupSpendOverComparisonCosts()
    {
        var buckets = new[]
        {
            Bucket(model: "gpt-5.4", cost: 4.00m, requests: 10),
            Bucket(model: "kimi-k2.5", cost: 1.50m, requests: 8)
        };
        var comparisons = new[]
        {
            Comparison(model: "kimi-k2.5", observed: 0.9, baseline: 0.7, cost: 99m)
        };

        var card = LearningReportCardAggregator.Aggregate(buckets, comparisons);

        Assert.Equal(5.50m, actual: card.TotalSpendUsd);
        Assert.Equal(expected: "gpt-5.4", actual: card.SpendByModel[0].Model);
        Assert.DoesNotContain(card.SpendByModel, row => row.CostUsd == 99m);
    }

    [Fact]
    public void Aggregate_FallsBackToComparisonCostsWhenRollupIsEmpty()
    {
        var comparisons = new[]
        {
            Comparison(model: "kimi-k2.5", observed: 0.9, baseline: 0.7, cost: 0.20m),
            Comparison(model: "kimi-k2.5", observed: 0.4, baseline: 0.5, cost: 0.10m),
            Comparison(model: "glm-5", observed: 0.2, baseline: null, cost: null)
        };

        var card = LearningReportCardAggregator.Aggregate([], comparisons);

        var kimi = Assert.Single(card.SpendByModel, row => row.Model == "kimi-k2.5");
        Assert.Equal(0.30m, actual: kimi.CostUsd);
        Assert.Equal(2, actual: kimi.Requests);
        var glm = Assert.Single(card.SpendByModel, row => row.Model == "glm-5");
        Assert.Equal(0m, actual: glm.CostUsd);
        Assert.Equal(1, actual: glm.Requests);
        Assert.Equal(0.30m, actual: card.TotalSpendUsd);
    }

    [Fact]
    public void Aggregate_AlwaysEmitsFiveGradeBucketsAndSkipsUnpredictedDeltas()
    {
        var comparisons = new[]
        {
            Comparison(model: "a", observed: 0.95, baseline: 0.80, cost: 0.01m),
            Comparison(model: "a", observed: 0.50, baseline: null, cost: 0.01m),
            Comparison(model: "b", observed: 0.10, baseline: 0.20, cost: 0.01m)
        };

        var card = LearningReportCardAggregator.Aggregate([], comparisons);

        Assert.Equal(LearningReportCardAggregator.GradeOrder, actual: card.GradeMix.Select(row => row.Grade));
        Assert.Equal(1, actual: card.GradeMix.Single(row => row.Grade == "A").Count);
        Assert.Equal(1, actual: card.GradeMix.Single(row => row.Grade == "C").Count);
        Assert.Equal(1, actual: card.GradeMix.Single(row => row.Grade == "F").Count);
        Assert.Equal(3, actual: card.ScoredRequests);
        Assert.Equal(2, actual: card.ComparableRequests);
        Assert.NotNull(card.MeanScoreDelta);
        Assert.Equal(0.025, actual: card.MeanScoreDelta!.Value, precision: 9);
        Assert.Equal(2, actual: card.ScoreDeltaByModel.Count);
        Assert.Equal(expected: "a", actual: card.ScoreDeltaByModel[0].Model);
        Assert.Equal(0.15, actual: card.ScoreDeltaByModel[0].MeanDelta, precision: 9);
        Assert.Equal(expected: "b", actual: card.ScoreDeltaByModel[1].Model);
        Assert.Equal(-0.10, actual: card.ScoreDeltaByModel[1].MeanDelta, precision: 9);
    }

    [Fact]
    public void Aggregate_EmptyInputs_AreZerosNotFabricatedDeltas()
    {
        var card = LearningReportCardAggregator.Aggregate([], []);

        Assert.Empty(card.SpendByModel);
        Assert.Equal(0m, actual: card.TotalSpendUsd);
        Assert.Equal(5, actual: card.GradeMix.Count);
        Assert.All(card.GradeMix, row => Assert.Equal(0, actual: row.Count));
        Assert.Null(card.MeanScoreDelta);
        Assert.Empty(card.ScoreDeltaByModel);
        Assert.Equal(0, actual: card.ComparableRequests);
    }

    private static UsageRollupBucket Bucket(string model, decimal cost, long requests)
    {
        return new UsageRollupBucket(
            BucketStartUtc: DateTimeOffset.UnixEpoch,
            BucketWidth: "P1D",
            GroupKey: model,
            Requests: requests,
            0,
            0, 0, 0, 0,
            CostUsd: cost);
    }

    private static TaxonomyComparisonRecord Comparison(
        string model, double observed, double? baseline, decimal? cost)
    {
        return new TaxonomyComparisonRecord(
            TranscriptId: 1,
            ComparedAtUtc: DateTimeOffset.UtcNow,
            SessionId: "s-1",
            ObservedScore: observed,
            0.7,
            0.7,
            0.1,
            0.1,
            true,
            false,
            RoutedModel: model,
            BaselineModel: baseline is null ? null : "glm-5",
            ActualCostUsd: cost,
            BaselineEstimatedCostUsd: cost,
            EstimatedNetSavingsUsd: 0m,
            BaselinePredictedScore: baseline,
            0);
    }
}
