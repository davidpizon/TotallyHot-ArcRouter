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
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var scores = new Dictionary<string, double>
        {
            ["kimi-k2.5"] = 0.8,
            ["gpt-5.4"] = 0.6
        };

        var decision = new RoutingDecision("kimi-k2.5", 0.8, "dimension-best prior", timestamp, scores);

        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Equal(0.8, decision.Confidence, 3);
        Assert.Equal("dimension-best prior", decision.Rationale);
        Assert.Equal(timestamp, decision.TimestampUtc);
        Assert.Equal(2, decision.CandidateScores.Count);
        Assert.False(decision.IsExploratory);
    }

    /// <summary>
    /// Verifies that an exploratory decision reports itself as such.
    /// </summary>
    [Fact]
    public void Constructor_IsExploratoryTrue_IsReflectedOnTheDecision()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var decision = new RoutingDecision(
            "kimi-k2.5", 0.5, "exploration", timestamp, candidateScores: null, isExploratory: true);

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
            new RoutingDecision("kimi-k2.5", 1.1, "invalid confidence", timestamp));
    }

    /// <summary>
    /// Verifies that fallback creation uses the expected fallback contract values.
    /// </summary>
    [Fact]
    public void CreateFallback_UsesFallbackContract()
    {
        var decision = RoutingDecision.CreateFallback("gpt-5.4");

        Assert.Equal("gpt-5.4", decision.SelectedModel);
        Assert.Equal(0, decision.Confidence);
        Assert.Equal(RouterConstants.FallbackReason, decision.Rationale);
        Assert.Empty(decision.CandidateScores);
        Assert.False(decision.IsExploratory);
    }
}

