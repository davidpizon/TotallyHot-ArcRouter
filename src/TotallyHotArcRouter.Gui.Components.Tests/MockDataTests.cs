using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Models;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="MockData"/>'s remaining offline fallbacks and, in particular,
/// <see cref="MockData.BuildMetricHistory"/>, the one non-trivial piece of logic here (a deterministic
/// synthetic-history generator consumed by the Cost Analytics tab).
/// </summary>
public sealed class MockDataTests
{
    [Fact]
    public void TokenBuckets_and_ModelShares_are_non_empty()
    {
        MockData.TokenBuckets.Should().NotBeEmpty();
        MockData.ModelShares.Should().NotBeEmpty();
        MockData.ReportCardSpend.Should().NotBeEmpty();
        MockData.ReportCardGradeMix.Should().HaveCount(5);
        MockData.ReportCardScoreDelta.Should().NotBeEmpty();
    }

    [Fact]
    public void BuildMetricHistory_is_deterministic_for_the_same_instant()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, offset: TimeSpan.Zero);

        var first = MockData.BuildMetricHistory(now);
        var second = MockData.BuildMetricHistory(now);

        first.Should().BeEquivalentTo(expectation: second, config: options => options.WithStrictOrdering());
    }

    [Fact]
    public void BuildMetricHistory_never_produces_points_after_now()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, offset: TimeSpan.Zero);

        var points = MockData.BuildMetricHistory(now);

        points.Should().NotBeEmpty();
        points.Should().OnlyContain(p => p.TimestampUtc <= now);
    }

    [Fact]
    public void BuildMetricHistory_spans_multiple_sessions_with_positive_metrics()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, offset: TimeSpan.Zero);

        var points = MockData.BuildMetricHistory(now);

        points.Select(p => p.SessionId).Distinct().Count().Should().BeGreaterThan(1);
        points.Should().OnlyContain(p => p.PromptTokens > 0);
        points.Should().OnlyContain(p => p.TotalCost >= 0);
        points.Should().OnlyContain(p => p.ContextBufferPercent <= 97m);
    }
}
