using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Covers constructor and fallback behavior for <see cref="RoutingDecision"/>.
/// </summary>
public class RoutingDecisionTests
{
    /// <summary>
    /// Verifies that a valid decision instance stores the expected values.
    /// </summary>
    [Fact]
    public void Constructor_SetsExpectedValues()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, offset: TimeSpan.Zero);
        var scores = new Dictionary<string, double>
        {
            ["kimi-k2.5"] = 0.8,
            ["gpt-5.4"] = 0.6
        };

        var decision = new RoutingDecision(selectedModel: "kimi-k2.5", 0.8, rationale: "dimension-best prior",
            timestampUtc: timestamp, candidateScores: scores);

        Assert.Equal(expected: "kimi-k2.5", actual: decision.SelectedModel);
        Assert.Equal(0.8, actual: decision.Confidence, 3);
        Assert.Equal(expected: "dimension-best prior", actual: decision.Rationale);
        Assert.Equal(expected: timestamp, actual: decision.TimestampUtc);
        Assert.Equal(2, actual: decision.CandidateScores.Count);
        Assert.False(decision.IsExploratory);
        Assert.Equal(1.0, actual: decision.Propensity, 6);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T1c: propensity is an explicit, additive
    /// optional constructor parameter that stores the value it was given.
    /// </summary>
    [Fact]
    public void Constructor_PropensityGiven_IsReflectedOnTheDecision()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, offset: TimeSpan.Zero);

        var decision = new RoutingDecision(
            selectedModel: "kimi-k2.5", 0.5, rationale: "exploration", timestampUtc: timestamp, null, true, 0.0167);

        Assert.Equal(0.0167, actual: decision.Propensity, 6);
    }

    /// <summary>
    /// Verifies that an exploratory decision reports itself as such.
    /// </summary>
    [Fact]
    public void Constructor_IsExploratoryTrue_IsReflectedOnTheDecision()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, offset: TimeSpan.Zero);

        var decision = new RoutingDecision(
            selectedModel: "kimi-k2.5", 0.5, rationale: "exploration", timestampUtc: timestamp, null, true);

        Assert.True(decision.IsExploratory);
    }

    /// <summary>
    /// Verifies that out-of-range confidence values are rejected.
    /// </summary>
    [Fact]
    public void Constructor_Throws_WhenConfidenceOutOfRange()
    {
        var timestamp = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RoutingDecision(selectedModel: "kimi-k2.5", 1.1, rationale: "invalid confidence",
                timestampUtc: timestamp));
    }

    /// <summary>
    /// Verifies that fallback creation uses the expected fallback contract values.
    /// </summary>
    [Fact]
    public void CreateFallback_UsesFallbackContract()
    {
        var decision = RoutingDecision.CreateFallback("gpt-5.4");

        Assert.Equal(expected: "gpt-5.4", actual: decision.SelectedModel);
        Assert.Equal(0, actual: decision.Confidence);
        Assert.Equal(expected: RouterConstants.FallbackReason, actual: decision.Rationale);
        Assert.Empty(decision.CandidateScores);
        Assert.False(decision.IsExploratory);
        Assert.Equal(1.0, actual: decision.Propensity, 6);
    }
}