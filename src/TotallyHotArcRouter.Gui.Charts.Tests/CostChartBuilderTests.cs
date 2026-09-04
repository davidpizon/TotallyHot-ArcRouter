namespace TotallyHot.ArcRouter.Gui.Charts.Tests;

/// <summary>
/// Covers <see cref="CostChartBuilder"/>: range/session filtering, per-metric chart kinds, and the
/// derived values/flags each bespoke format needs (signed ROI savings, cumulative cost/tokens, runaway
/// detection, step segmentation, latency spikes, and the context breach threshold).
/// </summary>
public class CostChartBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, offset: TimeSpan.Zero);

    private static MetricTurnPoint Point(
        DateTimeOffset timestamp,
        string sessionId = "s1",
        string model = "gpt-4o-mini",
        decimal? baselineCost = null,
        decimal cost = 0m,
        int promptTokens = 0,
        int completionTokens = 0,
        int toolSteps = 0,
        decimal cacheHit = 0m,
        int ttftMs = 0,
        decimal contextPct = 0m,
        string? baselineModel = null,
        bool isExploratory = false)
    {
        return new MetricTurnPoint(TimestampUtc: timestamp, SessionId: sessionId, Model: model,
            BaselineCostUsd: baselineCost,
            TotalCost: cost, PromptTokens: promptTokens, CompletionTokens: completionTokens,
            ToolExecutionSteps: toolSteps,
            CacheHitRate: cacheHit, TimeToFirstTokenMs: ttftMs, ContextBufferPercent: contextPct,
            BaselineModel: baselineModel, IsExploratory: isExploratory);
    }

    [Fact]
    public void Build_NullPoints_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CostChartBuilder.Build(points: null!, metric: CostMetric.Tokens, range: MetricRange.AllTime, null,
                now: Now));
    }

    [Fact]
    public void Build_EmptyPoints_ReturnsModelWithNoPoints()
    {
        var model = CostChartBuilder.Build(points: [], metric: CostMetric.Tokens, range: MetricRange.AllTime, null,
            now: Now);

        Assert.Empty(model.Points);
        Assert.Equal(expected: CostChartKind.RunawayArea, actual: model.Kind);
    }

    [Theory]
    [InlineData(CostMetric.RoutingRoi, CostChartKind.DualDirectionalBars, "$")]
    [InlineData(CostMetric.TotalTurnCost, CostChartKind.SteppedCumulativeArea, "$")]
    [InlineData(CostMetric.Tokens, CostChartKind.RunawayArea, "tok")]
    [InlineData(CostMetric.ToolExecutionSteps, CostChartKind.SegmentedStepBars, "steps")]
    [InlineData(CostMetric.CacheHitRate, CostChartKind.CacheGradientLine, "%")]
    [InlineData(CostMetric.TimeToFirstToken, CostChartKind.ZonedLatencyLine, "ms")]
    [InlineData(CostMetric.ContextBufferMargin, CostChartKind.ThresholdLine, "%")]
    public void Build_EachMetric_MapsToItsChartKindAndUnit(CostMetric metric, string kind, string unit)
    {
        var model = CostChartBuilder.Build(
            points: [Point(timestamp: Now.AddMinutes(-10), promptTokens: 100, toolSteps: 2)], metric: metric,
            range: MetricRange.Hour, null, now: Now);

        Assert.Equal(expected: kind, actual: model.Kind);
        Assert.Equal(expected: unit, actual: model.Unit);
    }

    [Fact]
    public void Build_HourRange_ExcludesTurnsOlderThanOneHour()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(timestamp: Now.AddMinutes(-90), promptTokens: 100), // outside the 1h window
            Point(timestamp: Now.AddMinutes(-30), promptTokens: 200), // inside
            Point(timestamp: Now.AddMinutes(-10), promptTokens: 300) // inside
        ];

        var model = CostChartBuilder.Build(points: points, metric: CostMetric.Tokens, range: MetricRange.Hour, null,
            now: Now);

        Assert.Equal(2, actual: model.Points.Count);
    }

    [Fact]
    public void Build_SessionFilter_KeepsOnlyThatSession()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(timestamp: Now.AddMinutes(-30), sessionId: "s1", promptTokens: 100),
            Point(timestamp: Now.AddMinutes(-20), sessionId: "s2", promptTokens: 999),
            Point(timestamp: Now.AddMinutes(-10), sessionId: "s1", promptTokens: 200)
        ];

        var model = CostChartBuilder.Build(points: points, metric: CostMetric.Tokens, range: MetricRange.Hour,
            sessionId: "s1", now: Now);

        Assert.Equal(2, actual: model.Points.Count);
    }

    [Fact]
    public void Build_UnsortedInput_ProducesChronologicalPoints()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(timestamp: Now.AddMinutes(-10), promptTokens: 300),
            Point(timestamp: Now.AddMinutes(-50), promptTokens: 100),
            Point(timestamp: Now.AddMinutes(-30), promptTokens: 200)
        ];

        var model = CostChartBuilder.Build(points: points, metric: CostMetric.Tokens, range: MetricRange.Hour, null,
            now: Now);

        Assert.True(model.Points[0].T < model.Points[1].T);
        Assert.True(model.Points[1].T < model.Points[2].T);
    }

    [Fact]
    public void Build_Tokens_AccumulatesCumulativeValue()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(timestamp: Now.AddMinutes(-30), promptTokens: 100, completionTokens: 50),
            Point(timestamp: Now.AddMinutes(-20), promptTokens: 200, completionTokens: 0)
        ];

        var model = CostChartBuilder.Build(points: points, metric: CostMetric.Tokens, range: MetricRange.Hour, null,
            now: Now);

        Assert.Equal(150m, actual: model.Points[0].Value);
        Assert.Equal(350m, actual: model.Points[1].Value);
    }

    [Fact]
    public void Build_Tokens_FlagsExponentialRunaway()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(timestamp: Now.AddMinutes(-30), promptTokens: 1000),
            Point(timestamp: Now.AddMinutes(-20), promptTokens: 1200), // modest growth, not a runaway
            Point(timestamp: Now.AddMinutes(-10), promptTokens: 100000) // > 2.5x the previous turn -> runaway
        ];

        var model = CostChartBuilder.Build(points: points, metric: CostMetric.Tokens, range: MetricRange.Hour, null,
            now: Now);

        Assert.False(model.Points[0].Flag);
        Assert.False(model.Points[1].Flag);
        Assert.True(model.Points[2].Flag);
        Assert.Contains(expectedSubstring: "RUNAWAY", actualString: model.Points[2].Label,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Cost_CarriesIncrementalAndCumulative()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(timestamp: Now.AddMinutes(-30), cost: 0.10m),
            Point(timestamp: Now.AddMinutes(-20), cost: 0.24m)
        ];

        var model = CostChartBuilder.Build(points: points, metric: CostMetric.TotalTurnCost, range: MetricRange.Hour,
            null, now: Now);

        Assert.Equal(0.10m, actual: model.Points[0].Value);
        Assert.Equal(0.10m, actual: model.Points[0].Secondary);
        Assert.Equal(0.24m, actual: model.Points[1].Value);
        Assert.Equal(0.34m, actual: model.Points[1].Secondary);
    }

    [Fact]
    public void Build_RoutingRoi_SavingIsBaselineMinusActual()
    {
        // A $2 turn against a $10 baseline estimate is an $8 saving - read straight off the counterfactual
        // rather than reconstructed from a percentage.
        var model = CostChartBuilder.Build(
            points: [Point(timestamp: Now.AddMinutes(-10), baselineCost: 10m, cost: 2m)],
            metric: CostMetric.RoutingRoi, range: MetricRange.Hour, null, now: Now);

        var p = Assert.Single(model.Points);
        Assert.Equal(8m, actual: p.Value);
        Assert.False(p.Flag);
    }

    [Fact]
    public void Build_RoutingRoi_CostlierThanBaselineIsANegativeFlaggedBar()
    {
        var model = CostChartBuilder.Build(
            points: [Point(timestamp: Now.AddMinutes(-10), baselineCost: 1m, cost: 3m)],
            metric: CostMetric.RoutingRoi, range: MetricRange.Hour, null, now: Now);

        var p = Assert.Single(model.Points);
        Assert.Equal(-2m, actual: p.Value);
        Assert.True(p.Flag);
    }

    [Fact]
    public void Build_RoutingRoi_TurnWithNoCounterfactualIsSkippedNotDrawnAtZero()
    {
        // The baseline abstained (or the comparison job has not reached this turn). A zero-height bar would
        // read as "routing broke even", which is a measurement rather than the absence of one.
        var model = CostChartBuilder.Build(
            points: [Point(timestamp: Now.AddMinutes(-10), baselineCost: null, cost: 3m)],
            metric: CostMetric.RoutingRoi, range: MetricRange.Hour, null, now: Now);

        Assert.Empty(model.Points);
    }

    [Fact]
    public void Build_RoutingRoi_LabelsEveryFigureAsAnEstimate()
    {
        var model = CostChartBuilder.Build(
            points: [Point(timestamp: Now.AddMinutes(-10), baselineCost: 10m, cost: 2m, baselineModel: "glm-5")],
            metric: CostMetric.RoutingRoi, range: MetricRange.Hour, null, now: Now);

        var p = Assert.Single(model.Points);
        Assert.Contains(collection: p.Tip,
            filter: line => line.Contains(value: "glm-5", comparisonType: StringComparison.Ordinal) &&
                            line.Contains(value: "estimate", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: p.Tip,
            filter: line => line.Contains(value: "Net saving", comparisonType: StringComparison.Ordinal) &&
                            line.Contains(value: "estimate", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RoutingRoi_ExploratoryTurnIsMutedAndLabelled()
    {
        var turns = new[]
        {
            Point(timestamp: Now.AddMinutes(-10), baselineCost: 10m, cost: 2m),
            Point(timestamp: Now.AddMinutes(-5), baselineCost: 10m, cost: 2m, isExploratory: true)
        };

        var model = CostChartBuilder.Build(points: turns, metric: CostMetric.RoutingRoi, range: MetricRange.Hour, null,
            now: Now);

        Assert.Equal(2, actual: model.Points.Count);
        // Same model, so the only difference is the exploratory muting - a probe must not read as a miss.
        Assert.NotEqual(expected: model.Points[0].Color, actual: model.Points[1].Color);
        Assert.Contains(collection: model.Points[1].Tip,
            filter: line => line.Contains(value: "Exploratory probe", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: model.Points[0].Tip,
            filter: line => line.Contains(value: "Ensemble pick", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RoutingRoi_ExploratoryTurnsStillCountTowardTheNetHeadline()
    {
        // Splitting them visually is not the same as excluding them: the headline is the all-in position.
        var turns = new[]
        {
            Point(timestamp: Now.AddMinutes(-10), baselineCost: 10m, cost: 2m),
            Point(timestamp: Now.AddMinutes(-5), baselineCost: 1m, cost: 4m, isExploratory: true)
        };

        var model = CostChartBuilder.Build(points: turns, metric: CostMetric.RoutingRoi, range: MetricRange.Hour, null,
            now: Now);

        // 8 saved, then 3 lost on the probe.
        Assert.Equal(expected: "$5.00", actual: model.Headline);
    }

    [Fact]
    public void Build_ToolSteps_SingleModelTurn_HasOneSegment()
    {
        var model = CostChartBuilder.Build(
            points: [Point(timestamp: Now.AddMinutes(-10), model: "a", toolSteps: 4)],
            metric: CostMetric.ToolExecutionSteps, range: MetricRange.Hour, null, now: Now);

        var p = Assert.Single(model.Points);
        var seg = Assert.Single(p.Segments!);
        Assert.Equal(expected: "a", actual: seg.Model);
        Assert.Equal(4, actual: seg.Steps);
    }

    [Fact]
    public void Build_ToolSteps_ModelHandoff_SplitsIntoTwoSegments()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(timestamp: Now.AddMinutes(-20), model: "a", toolSteps: 3),
            Point(timestamp: Now.AddMinutes(-10), model: "b", toolSteps: 5) // switched model -> segmented 2 + 3
        ];

        var model = CostChartBuilder.Build(points: points, metric: CostMetric.ToolExecutionSteps,
            range: MetricRange.Hour, null, now: Now);

        var second = model.Points[1];
        Assert.Equal(2, actual: second.Segments!.Count);
        Assert.Equal(expected: "a", actual: second.Segments[0].Model);
        Assert.Equal(expected: "b", actual: second.Segments[1].Model);
        Assert.Equal(5, actual: second.Segments.Sum(s => s.Steps));
        // Legend includes both models even though "a" is only present as a prior-turn segment.
        Assert.Contains(collection: model.Models, filter: m => m.Model == "a");
        Assert.Contains(collection: model.Models, filter: m => m.Model == "b");
    }

    [Fact]
    public void Build_Latency_FlagsSpikeAboveThreshold()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(timestamp: Now.AddMinutes(-20), ttftMs: 250),
            Point(timestamp: Now.AddMinutes(-10), ttftMs: 4850) // spike
        ];

        var model = CostChartBuilder.Build(points: points, metric: CostMetric.TimeToFirstToken, range: MetricRange.Hour,
            null, now: Now);

        Assert.False(model.Points[0].Flag);
        Assert.True(model.Points[1].Flag);
    }

    [Fact]
    public void Build_Context_FlagsBreachAtNinetyPercentAndSetsThreshold()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(timestamp: Now.AddMinutes(-20), contextPct: 60m),
            Point(timestamp: Now.AddMinutes(-10), contextPct: 92.1m) // breach
        ];

        var model = CostChartBuilder.Build(points: points, metric: CostMetric.ContextBufferMargin,
            range: MetricRange.Hour, null, now: Now);

        Assert.Equal(90m, actual: model.Threshold);
        Assert.False(model.Points[0].Flag);
        Assert.True(model.Points[1].Flag);
    }

    [Fact]
    public void Build_Cache_DerivesCachedAndUncachedTokensInTooltip()
    {
        var model = CostChartBuilder.Build(
            points: [Point(timestamp: Now.AddMinutes(-10), promptTokens: 1000, cacheHit: 80m)],
            metric: CostMetric.CacheHitRate, range: MetricRange.Hour, null, now: Now);

        var p = Assert.Single(model.Points);
        Assert.Equal(80m, actual: p.Value);
        Assert.Contains(collection: p.Tip,
            filter: l => l.Contains(value: "800", comparisonType: StringComparison.Ordinal)); // cached
        Assert.Contains(collection: p.Tip,
            filter: l => l.Contains(value: "200", comparisonType: StringComparison.Ordinal)); // uncached
    }

    [Fact]
    public void Build_AssignsDeterministicModelColors()
    {
        var model = CostChartBuilder.Build(
            points: [Point(timestamp: Now.AddMinutes(-10), model: "claude-3-haiku", promptTokens: 100)],
            metric: CostMetric.Tokens, range: MetricRange.Hour, null, now: Now);

        Assert.Equal(expected: ChartPalette.ColorFor("claude-3-haiku"), actual: model.Points[0].Color);
    }

    [Fact]
    public void CacheHitRate_ZeroInputTokens_ReturnsZero()
    {
        Assert.Equal(0m, actual: CostChartBuilder.CacheHitRate(0, 0, 0));
    }

    [Fact]
    public void CacheHitRate_FullyCachedTurn_ReturnsAtMostOneHundred()
    {
        var rate = CostChartBuilder.CacheHitRate(0, 0, 1000);

        Assert.Equal(100m, actual: rate);
        Assert.True(rate <= 100m);
    }

    [Fact]
    public void CacheHitRate_UsesAdditiveTotalAsDenominator()
    {
        // 500 read out of (100 prompt + 400 creation + 500 read) = 1000 total input tokens.
        var rate = CostChartBuilder.CacheHitRate(100, 400, 500);

        Assert.Equal(50m, actual: rate);
    }

    [Fact]
    public void CacheHitRate_NoCacheReadTokens_ReturnsZero()
    {
        var rate = CostChartBuilder.CacheHitRate(100, 0, 0);

        Assert.Equal(0m, actual: rate);
    }
}