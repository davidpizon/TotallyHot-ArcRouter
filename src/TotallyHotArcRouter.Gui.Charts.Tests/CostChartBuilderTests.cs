namespace TotallyHot.ArcRouter.Gui.Charts.Tests;

/// <summary>
/// Covers <see cref="CostChartBuilder"/>: range/session filtering, per-metric chart kinds, and the
/// derived values/flags each bespoke format needs (signed ROI savings, cumulative cost/tokens, runaway
/// detection, step segmentation, latency spikes, and the context breach threshold).
/// </summary>
public class CostChartBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

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
        bool isExploratory = false) =>
        new(timestamp, sessionId, model, baselineCost, cost, promptTokens, completionTokens, toolSteps,
            cacheHit, ttftMs, contextPct, baselineModel, isExploratory);

    [Fact]
    public void Build_NullPoints_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CostChartBuilder.Build(null!, CostMetric.Tokens, MetricRange.AllTime, null, Now));
    }

    [Fact]
    public void Build_EmptyPoints_ReturnsModelWithNoPoints()
    {
        var model = CostChartBuilder.Build([], CostMetric.Tokens, MetricRange.AllTime, null, Now);

        Assert.Empty(model.Points);
        Assert.Equal(CostChartKind.RunawayArea, model.Kind);
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
            [Point(Now.AddMinutes(-10), promptTokens: 100, toolSteps: 2)], metric, MetricRange.Hour, null, Now);

        Assert.Equal(kind, model.Kind);
        Assert.Equal(unit, model.Unit);
    }

    [Fact]
    public void Build_HourRange_ExcludesTurnsOlderThanOneHour()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(Now.AddMinutes(-90), promptTokens: 100), // outside the 1h window
            Point(Now.AddMinutes(-30), promptTokens: 200), // inside
            Point(Now.AddMinutes(-10), promptTokens: 300), // inside
        ];

        var model = CostChartBuilder.Build(points, CostMetric.Tokens, MetricRange.Hour, null, Now);

        Assert.Equal(2, model.Points.Count);
    }

    [Fact]
    public void Build_SessionFilter_KeepsOnlyThatSession()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(Now.AddMinutes(-30), sessionId: "s1", promptTokens: 100),
            Point(Now.AddMinutes(-20), sessionId: "s2", promptTokens: 999),
            Point(Now.AddMinutes(-10), sessionId: "s1", promptTokens: 200),
        ];

        var model = CostChartBuilder.Build(points, CostMetric.Tokens, MetricRange.Hour, "s1", Now);

        Assert.Equal(2, model.Points.Count);
    }

    [Fact]
    public void Build_UnsortedInput_ProducesChronologicalPoints()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(Now.AddMinutes(-10), promptTokens: 300),
            Point(Now.AddMinutes(-50), promptTokens: 100),
            Point(Now.AddMinutes(-30), promptTokens: 200),
        ];

        var model = CostChartBuilder.Build(points, CostMetric.Tokens, MetricRange.Hour, null, Now);

        Assert.True(model.Points[0].T < model.Points[1].T);
        Assert.True(model.Points[1].T < model.Points[2].T);
    }

    [Fact]
    public void Build_Tokens_AccumulatesCumulativeValue()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(Now.AddMinutes(-30), promptTokens: 100, completionTokens: 50),
            Point(Now.AddMinutes(-20), promptTokens: 200, completionTokens: 0),
        ];

        var model = CostChartBuilder.Build(points, CostMetric.Tokens, MetricRange.Hour, null, Now);

        Assert.Equal(150m, model.Points[0].Value);
        Assert.Equal(350m, model.Points[1].Value);
    }

    [Fact]
    public void Build_Tokens_FlagsExponentialRunaway()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(Now.AddMinutes(-30), promptTokens: 1000),
            Point(Now.AddMinutes(-20), promptTokens: 1200),   // modest growth, not a runaway
            Point(Now.AddMinutes(-10), promptTokens: 100000),  // > 2.5x the previous turn -> runaway
        ];

        var model = CostChartBuilder.Build(points, CostMetric.Tokens, MetricRange.Hour, null, Now);

        Assert.False(model.Points[0].Flag);
        Assert.False(model.Points[1].Flag);
        Assert.True(model.Points[2].Flag);
        Assert.Contains("RUNAWAY", model.Points[2].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Cost_CarriesIncrementalAndCumulative()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(Now.AddMinutes(-30), cost: 0.10m),
            Point(Now.AddMinutes(-20), cost: 0.24m),
        ];

        var model = CostChartBuilder.Build(points, CostMetric.TotalTurnCost, MetricRange.Hour, null, Now);

        Assert.Equal(0.10m, model.Points[0].Value);
        Assert.Equal(0.10m, model.Points[0].Secondary);
        Assert.Equal(0.24m, model.Points[1].Value);
        Assert.Equal(0.34m, model.Points[1].Secondary);
    }

    [Fact]
    public void Build_RoutingRoi_SavingIsBaselineMinusActual()
    {
        // A $2 turn against a $10 baseline estimate is an $8 saving - read straight off the counterfactual
        // rather than reconstructed from a percentage.
        var model = CostChartBuilder.Build(
            [Point(Now.AddMinutes(-10), baselineCost: 10m, cost: 2m)],
            CostMetric.RoutingRoi, MetricRange.Hour, null, Now);

        var p = Assert.Single(model.Points);
        Assert.Equal(8m, p.Value);
        Assert.False(p.Flag);
    }

    [Fact]
    public void Build_RoutingRoi_CostlierThanBaselineIsANegativeFlaggedBar()
    {
        var model = CostChartBuilder.Build(
            [Point(Now.AddMinutes(-10), baselineCost: 1m, cost: 3m)],
            CostMetric.RoutingRoi, MetricRange.Hour, null, Now);

        var p = Assert.Single(model.Points);
        Assert.Equal(-2m, p.Value);
        Assert.True(p.Flag);
    }

    [Fact]
    public void Build_RoutingRoi_TurnWithNoCounterfactualIsSkippedNotDrawnAtZero()
    {
        // The baseline abstained (or the comparison job has not reached this turn). A zero-height bar would
        // read as "routing broke even", which is a measurement rather than the absence of one.
        var model = CostChartBuilder.Build(
            [Point(Now.AddMinutes(-10), baselineCost: null, cost: 3m)],
            CostMetric.RoutingRoi, MetricRange.Hour, null, Now);

        Assert.Empty(model.Points);
    }

    [Fact]
    public void Build_RoutingRoi_LabelsEveryFigureAsAnEstimate()
    {
        var model = CostChartBuilder.Build(
            [Point(Now.AddMinutes(-10), baselineCost: 10m, cost: 2m, baselineModel: "glm-5")],
            CostMetric.RoutingRoi, MetricRange.Hour, null, Now);

        var p = Assert.Single(model.Points);
        Assert.Contains(p.Tip, line => line.Contains("glm-5", StringComparison.Ordinal) && line.Contains("estimate", StringComparison.Ordinal));
        Assert.Contains(p.Tip, line => line.Contains("Net saving", StringComparison.Ordinal) && line.Contains("estimate", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RoutingRoi_ExploratoryTurnIsMutedAndLabelled()
    {
        var turns = new[]
        {
            Point(Now.AddMinutes(-10), baselineCost: 10m, cost: 2m),
            Point(Now.AddMinutes(-5), baselineCost: 10m, cost: 2m, isExploratory: true),
        };

        var model = CostChartBuilder.Build(turns, CostMetric.RoutingRoi, MetricRange.Hour, null, Now);

        Assert.Equal(2, model.Points.Count);
        // Same model, so the only difference is the exploratory muting - a probe must not read as a miss.
        Assert.NotEqual(model.Points[0].Color, model.Points[1].Color);
        Assert.Contains(model.Points[1].Tip, line => line.Contains("Exploratory probe", StringComparison.Ordinal));
        Assert.Contains(model.Points[0].Tip, line => line.Contains("Ensemble pick", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RoutingRoi_ExploratoryTurnsStillCountTowardTheNetHeadline()
    {
        // Splitting them visually is not the same as excluding them: the headline is the all-in position.
        var turns = new[]
        {
            Point(Now.AddMinutes(-10), baselineCost: 10m, cost: 2m),
            Point(Now.AddMinutes(-5), baselineCost: 1m, cost: 4m, isExploratory: true),
        };

        var model = CostChartBuilder.Build(turns, CostMetric.RoutingRoi, MetricRange.Hour, null, Now);

        // 8 saved, then 3 lost on the probe.
        Assert.Equal("$5.00", model.Headline);
    }

    [Fact]
    public void Build_ToolSteps_SingleModelTurn_HasOneSegment()
    {
        var model = CostChartBuilder.Build(
            [Point(Now.AddMinutes(-10), model: "a", toolSteps: 4)],
            CostMetric.ToolExecutionSteps, MetricRange.Hour, null, Now);

        var p = Assert.Single(model.Points);
        var seg = Assert.Single(p.Segments!);
        Assert.Equal("a", seg.Model);
        Assert.Equal(4, seg.Steps);
    }

    [Fact]
    public void Build_ToolSteps_ModelHandoff_SplitsIntoTwoSegments()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(Now.AddMinutes(-20), model: "a", toolSteps: 3),
            Point(Now.AddMinutes(-10), model: "b", toolSteps: 5), // switched model -> segmented 2 + 3
        ];

        var model = CostChartBuilder.Build(points, CostMetric.ToolExecutionSteps, MetricRange.Hour, null, Now);

        var second = model.Points[1];
        Assert.Equal(2, second.Segments!.Count);
        Assert.Equal("a", second.Segments[0].Model);
        Assert.Equal("b", second.Segments[1].Model);
        Assert.Equal(5, second.Segments.Sum(s => s.Steps));
        // Legend includes both models even though "a" is only present as a prior-turn segment.
        Assert.Contains(model.Models, m => m.Model == "a");
        Assert.Contains(model.Models, m => m.Model == "b");
    }

    [Fact]
    public void Build_Latency_FlagsSpikeAboveThreshold()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(Now.AddMinutes(-20), ttftMs: 250),
            Point(Now.AddMinutes(-10), ttftMs: 4850), // spike
        ];

        var model = CostChartBuilder.Build(points, CostMetric.TimeToFirstToken, MetricRange.Hour, null, Now);

        Assert.False(model.Points[0].Flag);
        Assert.True(model.Points[1].Flag);
    }

    [Fact]
    public void Build_Context_FlagsBreachAtNinetyPercentAndSetsThreshold()
    {
        IReadOnlyList<MetricTurnPoint> points =
        [
            Point(Now.AddMinutes(-20), contextPct: 60m),
            Point(Now.AddMinutes(-10), contextPct: 92.1m), // breach
        ];

        var model = CostChartBuilder.Build(points, CostMetric.ContextBufferMargin, MetricRange.Hour, null, Now);

        Assert.Equal(90m, model.Threshold);
        Assert.False(model.Points[0].Flag);
        Assert.True(model.Points[1].Flag);
    }

    [Fact]
    public void Build_Cache_DerivesCachedAndUncachedTokensInTooltip()
    {
        var model = CostChartBuilder.Build(
            [Point(Now.AddMinutes(-10), promptTokens: 1000, cacheHit: 80m)],
            CostMetric.CacheHitRate, MetricRange.Hour, null, Now);

        var p = Assert.Single(model.Points);
        Assert.Equal(80m, p.Value);
        Assert.Contains(p.Tip, l => l.Contains("800", StringComparison.Ordinal));  // cached
        Assert.Contains(p.Tip, l => l.Contains("200", StringComparison.Ordinal));  // uncached
    }

    [Fact]
    public void Build_AssignsDeterministicModelColors()
    {
        var model = CostChartBuilder.Build(
            [Point(Now.AddMinutes(-10), model: "claude-3-haiku", promptTokens: 100)],
            CostMetric.Tokens, MetricRange.Hour, null, Now);

        Assert.Equal(ChartPalette.ColorFor("claude-3-haiku"), model.Points[0].Color);
    }

    [Fact]
    public void CacheHitRate_ZeroInputTokens_ReturnsZero()
    {
        Assert.Equal(0m, CostChartBuilder.CacheHitRate(promptTokens: 0, cacheCreationTokens: 0, cacheReadTokens: 0));
    }

    [Fact]
    public void CacheHitRate_FullyCachedTurn_ReturnsAtMostOneHundred()
    {
        var rate = CostChartBuilder.CacheHitRate(promptTokens: 0, cacheCreationTokens: 0, cacheReadTokens: 1000);

        Assert.Equal(100m, rate);
        Assert.True(rate <= 100m);
    }

    [Fact]
    public void CacheHitRate_UsesAdditiveTotalAsDenominator()
    {
        // 500 read out of (100 prompt + 400 creation + 500 read) = 1000 total input tokens.
        var rate = CostChartBuilder.CacheHitRate(promptTokens: 100, cacheCreationTokens: 400, cacheReadTokens: 500);

        Assert.Equal(50m, rate);
    }

    [Fact]
    public void CacheHitRate_NoCacheReadTokens_ReturnsZero()
    {
        var rate = CostChartBuilder.CacheHitRate(promptTokens: 100, cacheCreationTokens: 0, cacheReadTokens: 0);

        Assert.Equal(0m, rate);
    }
}

