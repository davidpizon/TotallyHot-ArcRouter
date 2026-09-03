using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="PersistedSessionMapper"/>: the "real vs. honestly-defaulted" field mapping
/// documented on the class, the title/short-id formatting rules, and the
/// <see cref="TotallyHot.ArcRouter.Gui.Models.Conversation.IsUsedForTraining"/> pass-through
/// (docs/router/sessions-tab-training-data-plan.md Phase 2).
/// </summary>
public sealed class PersistedSessionMapperTests
{
    [Fact]
    public void ToModel_throws_on_null_conversation()
    {
        var act = () => PersistedSessionMapper.ToModel(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToModel_maps_conversation_level_fields_through()
    {
        var conversation = new PersistedConversation(
            SessionId: "session-abcdef123",
            FirstTimestampUtc: new DateTimeOffset(2026, 7, 16, 10, 0, 0, offset: TimeSpan.Zero),
            LastTimestampUtc: new DateTimeOffset(2026, 7, 16, 10, 5, 0, offset: TimeSpan.Zero),
            1.5m,
            100,
            50,
            Turns: [],
            true);

        var model = PersistedSessionMapper.ToModel(conversation);

        model.Id.Should().Be("session-abcdef123");
        model.Title.Should().Be("Session session-");
        model.TotalCost.Should().Be(1.5m);
        model.TotalPromptTokens.Should().Be(100);
        model.TotalCompletionTokens.Should().Be(50);
        model.HasFallbackTurns.Should().BeFalse();
        model.Turns.Should().BeEmpty();
        model.IsUsedForTraining.Should().BeTrue();
    }

    [Fact]
    public void ToModel_notUsedForTraining_carriesFalseThrough()
    {
        var conversation = new PersistedConversation(
            SessionId: "abc",
            FirstTimestampUtc: DateTimeOffset.UtcNow,
            LastTimestampUtc: DateTimeOffset.UtcNow,
            0,
            0,
            0,
            Turns: [],
            false);

        var model = PersistedSessionMapper.ToModel(conversation);

        model.IsUsedForTraining.Should().BeFalse();
        // Session id is <= 8 chars, so ShortId returns it unshortened.
        model.Title.Should().Be("Session abc");
    }

    [Fact]
    public void ToModel_maps_turn_level_fields_with_honest_defaults()
    {
        var turn = new PersistedConversationTurn(
            CorrelationId: "s1:3",
            3,
            RequestedModel: "gpt-5.4",
            RoutedModel: "kimi-k2.5",
            PromptText: "Hello",
            ResponseText: "Hi there",
            0.05m,
            200,
            80,
            TimestampUtc: new DateTimeOffset(2026, 7, 16, 9, 30, 15, offset: TimeSpan.Zero),
            null);

        var conversation = new PersistedConversation(
            SessionId: "s1",
            FirstTimestampUtc: turn.TimestampUtc,
            LastTimestampUtc: turn.TimestampUtc,
            TotalCostUsd: turn.CostUsd!.Value,
            TotalInputTokens: turn.InputTokens!.Value,
            TotalOutputTokens: turn.OutputTokens!.Value,
            Turns: [turn],
            false);

        var model = PersistedSessionMapper.ToModel(conversation);
        var mappedTurn = model.Turns.Should().ContainSingle().Subject;

        mappedTurn.Id.Should().Be("s1:3");
        mappedTurn.Agent.Should().Be("kimi-k2.5");
        mappedTurn.Model.Should().Be("kimi-k2.5");
        mappedTurn.TurnNumber.Should().Be(3);
        mappedTurn.PromptTokens.Should().Be(200);
        mappedTurn.CompletionTokens.Should().Be(80);
        mappedTurn.TotalCost.Should().Be(0.05m);
        mappedTurn.RequestSummary.Should().Be("Hello");
        mappedTurn.ResponseSummary.Should().Be("Hi there");
        mappedTurn.RequestedModel.Should().Be("gpt-5.4");
        mappedTurn.RoutedModel.Should().Be("kimi-k2.5");
        mappedTurn.IsFallback.Should().BeFalse();
        mappedTurn.TimestampUtc.Should().Be(turn.TimestampUtc);
        // Honestly-defaulted: request_transcripts carries none of these.
        mappedTurn.RoutingRoi.Should().Be(0m);
        mappedTurn.ToolExecutionSteps.Should().Be(0);
        mappedTurn.CacheHitRate.Should().Be(0m);
        mappedTurn.TimeToFirstTokenMs.Should().Be(0);
        mappedTurn.ContextBufferPercent.Should().Be(0m);
    }

    [Fact]
    public void ToModel_turnWithNoCostOrTokens_defaultsToZeroRatherThanNull()
    {
        var turn = new PersistedConversationTurn(
            CorrelationId: "s1:1",
            1,
            RequestedModel: "gpt-5.4",
            RoutedModel: "gpt-5.4",
            null,
            null,
            null,
            null,
            null,
            TimestampUtc: DateTimeOffset.UtcNow,
            null);

        var conversation = new PersistedConversation(
            SessionId: "s1",
            FirstTimestampUtc: turn.TimestampUtc,
            LastTimestampUtc: turn.TimestampUtc,
            0,
            0,
            0,
            Turns: [turn],
            false);

        var model = PersistedSessionMapper.ToModel(conversation);
        var mappedTurn = model.Turns.Should().ContainSingle().Subject;

        mappedTurn.PromptTokens.Should().Be(0);
        mappedTurn.CompletionTokens.Should().Be(0);
        mappedTurn.TotalCost.Should().Be(0m);
    }
}