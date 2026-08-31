using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using Bunit;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Tests for <see cref="SessionConversationPane"/>: chat-bubble reproduction of a session's
/// turns in ascending turn-number order, and the muted placeholder for a turn missing a request or
/// response summary.</summary>
public sealed class SessionConversationPaneTests
{
    private static ConversationTurn MakeTurn(int turnNumber, string? requestSummary, string? responseSummary) => new(
        Id: $"t{turnNumber}",
        Agent: "Agent A",
        Model: "model-a",
        TurnNumber: turnNumber,
        PromptTokens: 10,
        CompletionTokens: 5,
        RoutingRoi: 0,
        TotalCost: 0,
        ToolExecutionSteps: 0,
        CacheHitRate: 0,
        TimeToFirstTokenMs: 0,
        ContextBufferPercent: 0,
        Timestamp: "10:00:00",
        RoutingSteps: [],
        RequestSummary: requestSummary,
        ResponseSummary: responseSummary);

    private static Conversation MakeConversation(params ConversationTurn[] turns) => new(
        Id: "sess-1",
        Title: "Test Conversation",
        FirstTimestamp: "10:00:00",
        LastTimestamp: "10:05:00",
        TotalCost: 0,
        TotalPromptTokens: 0,
        TotalCompletionTokens: 0,
        HasFallbackTurns: false,
        Turns: turns);

    [Fact]
    public void Renders_request_and_response_bubbles_for_each_turn()
    {
        using var ctx = new Bunit.BunitContext();

        var conversation = MakeConversation(
            MakeTurn(1, "What broke?", "The cache went stale."));

        var cut = ctx.Render<SessionConversationPane>(p => p.Add(c => c.Conversation, conversation));

        cut.Markup.Should().Contain("What broke?");
        cut.Markup.Should().Contain("The cache went stale.");
        cut.Markup.Should().Contain("Turn 1");
    }

    [Fact]
    public void Renders_turns_in_ascending_turn_number_order_regardless_of_input_order()
    {
        using var ctx = new Bunit.BunitContext();

        var conversation = MakeConversation(
            MakeTurn(2, "Second request", "Second response"),
            MakeTurn(1, "First request", "First response"));

        var cut = ctx.Render<SessionConversationPane>(p => p.Add(c => c.Conversation, conversation));

        var firstIndex = cut.Markup.IndexOf("First request", StringComparison.Ordinal);
        var secondIndex = cut.Markup.IndexOf("Second request", StringComparison.Ordinal);

        firstIndex.Should().BeGreaterThan(-1);
        secondIndex.Should().BeGreaterThan(-1);
        firstIndex.Should().BeLessThan(secondIndex);
    }

    [Fact]
    public void Shows_muted_placeholders_when_a_turn_has_no_request_or_response_summary()
    {
        using var ctx = new Bunit.BunitContext();

        var conversation = MakeConversation(MakeTurn(1, requestSummary: null, responseSummary: null));

        var cut = ctx.Render<SessionConversationPane>(p => p.Add(c => c.Conversation, conversation));

        cut.Markup.Should().Contain("No request captured");
        cut.Markup.Should().Contain("No response captured");
    }
}
