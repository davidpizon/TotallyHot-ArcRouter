using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="LiveConversationMapper"/>: verifies the "real vs. honestly-defaulted" field
/// mapping documented on the class, plus the title/short-id formatting rules.
/// </summary>
public sealed class LiveConversationMapperTests
{
    [Fact]
    public void ToModel_throws_on_null_conversation()
    {
        var act = () => LiveConversationMapper.ToModel(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToModel_maps_conversation_level_fields_through()
    {
        var conversation = new LiveConversation(
            SessionId: "session-abcdef123",
            false,
            FirstTimestampUtc: new DateTimeOffset(2026, 7, 16, 10, 0, 0, offset: TimeSpan.Zero),
            LastTimestampUtc: new DateTimeOffset(2026, 7, 16, 10, 5, 0, offset: TimeSpan.Zero),
            1.5m,
            100,
            50,
            true,
            Turns: []);

        var model = LiveConversationMapper.ToModel(conversation);

        model.Id.Should().Be("session-abcdef123");
        model.Title.Should().Be("Session session-");
        model.TotalCost.Should().Be(1.5m);
        model.TotalPromptTokens.Should().Be(100);
        model.TotalCompletionTokens.Should().Be(50);
        model.HasFallbackTurns.Should().BeTrue();
        model.Turns.Should().BeEmpty();
    }

    [Fact]
    public void ToModel_marks_synthesized_sessions_as_untracked_in_the_title()
    {
        var conversation = new LiveConversation(
            SessionId: "abc",
            true,
            FirstTimestampUtc: DateTimeOffset.UtcNow,
            LastTimestampUtc: DateTimeOffset.UtcNow,
            0,
            0,
            0,
            false,
            Turns: []);

        var model = LiveConversationMapper.ToModel(conversation);

        // Session id is <= 8 chars, so ShortId returns it unshortened.
        model.Title.Should().Be("Untracked session (abc)");
    }

    [Fact]
    public void ToModel_maps_turn_level_fields_with_honest_defaults()
    {
        var turn = new LiveConversationTurn(
            SessionId: "s1",
            3,
            Agent: "gpt-4o-mini",
            Model: "gpt-4o-mini",
            200,
            80,
            0.05m,
            false,
            340,
            TimestampUtc: new DateTimeOffset(2026, 7, 16, 9, 30, 15, offset: TimeSpan.Zero),
            RequestSummary: "Hello",
            ResponseSummary: "Hi there");

        var conversation = new LiveConversation(
            SessionId: "s1",
            false,
            FirstTimestampUtc: turn.TimestampUtc,
            LastTimestampUtc: turn.TimestampUtc,
            TotalCost: turn.EstimatedCostUsd,
            TotalPromptTokens: turn.PromptTokens,
            TotalCompletionTokens: turn.CompletionTokens,
            false,
            Turns: [turn]);

        var model = LiveConversationMapper.ToModel(conversation);
        var mappedTurn = model.Turns.Should().ContainSingle().Subject;

        mappedTurn.Id.Should().Be("s1-t3");
        mappedTurn.Agent.Should().Be("gpt-4o-mini");
        mappedTurn.Model.Should().Be("gpt-4o-mini");
        mappedTurn.TurnNumber.Should().Be(3);
        mappedTurn.PromptTokens.Should().Be(200);
        mappedTurn.CompletionTokens.Should().Be(80);
        mappedTurn.TotalCost.Should().Be(0.05m);
        mappedTurn.TimeToFirstTokenMs.Should().Be(340);
        mappedTurn.RequestSummary.Should().Be("Hello");
        mappedTurn.ResponseSummary.Should().Be("Hi there");
        mappedTurn.TimestampUtc.Should().Be(turn.TimestampUtc);

        // Documented honest defaults - no live-data source exists for these yet.
        mappedTurn.RoutingRoi.Should().Be(0m);
        mappedTurn.ToolExecutionSteps.Should().Be(0);
        mappedTurn.ContextBufferPercent.Should().Be(0m);

        // No cache tokens on this turn, so the real derived rate is 0 too.
        mappedTurn.CacheHitRate.Should().Be(0m);

        mappedTurn.RoutingSteps.Should().ContainSingle()
            .Which.Message.Should().Be("Route Confirmed: gpt-4o-mini");
    }

    [Fact]
    public void ToModel_derives_cache_hit_rate_from_the_turns_cache_token_counts()
    {
        var turn = new LiveConversationTurn(
            SessionId: "s1",
            1,
            Agent: "claude-sonnet-5",
            Model: "claude-sonnet-5",
            100,
            20,
            0.02m,
            false,
            200,
            TimestampUtc: DateTimeOffset.UtcNow,
            400,
            500);

        var conversation = new LiveConversation(
            SessionId: "s1",
            false,
            FirstTimestampUtc: turn.TimestampUtc,
            LastTimestampUtc: turn.TimestampUtc,
            TotalCost: turn.EstimatedCostUsd,
            TotalPromptTokens: turn.PromptTokens,
            TotalCompletionTokens: turn.CompletionTokens,
            false,
            Turns: [turn]);

        var model = LiveConversationMapper.ToModel(conversation);
        var mappedTurn = model.Turns.Should().ContainSingle().Subject;

        // 500 read out of (100 prompt + 400 creation + 500 read) = 1000 total input tokens.
        mappedTurn.CacheHitRate.Should().Be(50m);
    }

    [Fact]
    public void ToModel_adds_a_fallback_warning_step_when_the_turn_used_fallback_routing()
    {
        var turn = new LiveConversationTurn(
            SessionId: "s1",
            1,
            Agent: "local",
            Model: "local",
            10,
            5,
            0m,
            true,
            -5, // clamps to 0 below
            TimestampUtc: DateTimeOffset.UtcNow);

        var conversation = new LiveConversation(
            SessionId: "s1",
            false,
            FirstTimestampUtc: turn.TimestampUtc,
            LastTimestampUtc: turn.TimestampUtc,
            0,
            10,
            5,
            true,
            Turns: [turn]);

        var model = LiveConversationMapper.ToModel(conversation);
        var mappedTurn = model.Turns.Should().ContainSingle().Subject;

        mappedTurn.IsFallback.Should().BeTrue();
        mappedTurn.TimeToFirstTokenMs.Should().Be(0);
        mappedTurn.RoutingSteps.Should().HaveCount(2);
        mappedTurn.RoutingSteps[0].Status.Should().Be(StepStatus.Warn);
        mappedTurn.RoutingSteps[0].Message.Should().Be("Fallback routing was used for this turn.");
        mappedTurn.RoutingSteps[1].Status.Should().Be(StepStatus.Info);
    }

    [Theory]
    [InlineData("UnresolvedName")]
    [InlineData("ModelStopped")]
    [InlineData("CircuitOpen")]
    [InlineData("Failover")]
    public void ToModel_adds_a_substitution_warning_step_for_a_visible_substitution_reason(string reason)
    {
        var turn = new LiveConversationTurn(
            SessionId: "s1",
            1,
            Agent: "claude-sonnet-5",
            Model: "claude-sonnet-5",
            10,
            5,
            0.01m,
            false,
            100,
            TimestampUtc: DateTimeOffset.UtcNow,
            RequestedModel: "gpt-4o",
            RoutedModel: "claude-sonnet-5",
            SubstitutionReason: reason);

        var conversation = new LiveConversation(
            SessionId: "s1",
            false,
            FirstTimestampUtc: turn.TimestampUtc,
            LastTimestampUtc: turn.TimestampUtc,
            TotalCost: turn.EstimatedCostUsd,
            TotalPromptTokens: turn.PromptTokens,
            TotalCompletionTokens: turn.CompletionTokens,
            false,
            Turns: [turn]);

        var model = LiveConversationMapper.ToModel(conversation);
        var mappedTurn = model.Turns.Should().ContainSingle().Subject;

        mappedTurn.RequestedModel.Should().Be("gpt-4o");
        mappedTurn.RoutedModel.Should().Be("claude-sonnet-5");
        mappedTurn.SubstitutionReason.Should().Be(reason);
        mappedTurn.RoutingSteps.Should().HaveCount(2);
        mappedTurn.RoutingSteps[0].Status.Should().Be(StepStatus.Warn);
        mappedTurn.RoutingSteps[0].Message.Should().Be($"Requested gpt-4o → routed to claude-sonnet-5 ({reason}).");
        mappedTurn.RoutingSteps[1].Message.Should().Be("Route Confirmed: claude-sonnet-5");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("None")]
    [InlineData("AutoSelect")]
    public void ToModel_omits_the_substitution_step_when_the_reason_is_not_visible(string? reason)
    {
        var turn = new LiveConversationTurn(
            SessionId: "s1",
            1,
            Agent: "claude-sonnet-5",
            Model: "claude-sonnet-5",
            10,
            5,
            0.01m,
            false,
            100,
            TimestampUtc: DateTimeOffset.UtcNow,
            RequestedModel: "auto",
            RoutedModel: "claude-sonnet-5",
            SubstitutionReason: reason);

        var conversation = new LiveConversation(
            SessionId: "s1",
            false,
            FirstTimestampUtc: turn.TimestampUtc,
            LastTimestampUtc: turn.TimestampUtc,
            TotalCost: turn.EstimatedCostUsd,
            TotalPromptTokens: turn.PromptTokens,
            TotalCompletionTokens: turn.CompletionTokens,
            false,
            Turns: [turn]);

        var model = LiveConversationMapper.ToModel(conversation);
        var mappedTurn = model.Turns.Should().ContainSingle().Subject;

        mappedTurn.RoutingSteps.Should().ContainSingle()
            .Which.Message.Should().Be("Route Confirmed: claude-sonnet-5");
    }
}