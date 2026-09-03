using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Models;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="MockData"/>'s static fixtures and, in particular,
/// <see cref="MockData.BuildMetricHistory"/>, the one non-trivial piece of logic here (a deterministic
/// synthetic-history generator consumed by the Cost Analytics tab).
/// </summary>
public sealed class MockDataTests
{
    [Fact]
    public void Conversations_entries_and_fixtures_are_non_empty()
    {
        MockData.Conversations.Should().NotBeEmpty();
        MockData.Entries.Should().NotBeEmpty();
        MockData.CostData.Should().NotBeEmpty();
        MockData.AgentRoi.Should().NotBeEmpty();
        MockData.TokenBuckets.Should().NotBeEmpty();
        MockData.ModelShares.Should().NotBeEmpty();
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

    [Fact]
    public void Conversation_records_expose_the_expected_shape()
    {
        var conversation = MockData.Conversations[0];

        conversation.Turns.Should().NotBeEmpty();
        var turn = conversation.Turns[0];
        turn.Id.Should().NotBeNullOrEmpty();
        turn.RoutingSteps.Should().NotBeEmpty();
    }

    [Fact]
    public void RoutingEntry_record_equality_compares_all_fields()
    {
        var steps = new List<RoutingStep> { new(Status: StepStatus.Ok, Message: "Agent selected model") };
        var a = new RoutingEntry(Id: "1", SessionId: "session", TraceId: "trace", Agent: "Agent", Model: "gpt-4o-mini",
            false, 100, 50, 0.01m, 0.05m, 0.04m, 80m, Timestamp: "14:32:01", RoutingSteps: steps);
        var b = new RoutingEntry(Id: "1", SessionId: "session", TraceId: "trace", Agent: "Agent", Model: "gpt-4o-mini",
            false, 100, 50, 0.01m, 0.05m, 0.04m, 80m, Timestamp: "14:32:01", RoutingSteps: steps);
        var differentModel = a with { Model = "gpt-4o" };

        a.Should().Be(b);
        a.Should().NotBe(differentModel);
        a.ToString().Should().Contain("gpt-4o-mini");
    }

    [Fact]
    public void Entries_expose_the_expected_shape()
    {
        var entry = MockData.Entries[0];

        entry.Id.Should().NotBeNullOrEmpty();
        entry.RoutingSteps.Should().NotBeEmpty();
    }
}