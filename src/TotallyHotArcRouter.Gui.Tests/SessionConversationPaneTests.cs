using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="SessionConversationPane"/>: chat-bubble reproduction of a session's
/// turns in ascending turn-number order, and the muted placeholder for a turn missing a request or
/// response summary.
/// </summary>
public sealed class SessionConversationPaneTests
{
    private static ConversationTurn MakeTurn(int turnNumber, string? requestSummary, string? responseSummary)
    {
        return new ConversationTurn(
            Id: $"t{turnNumber}",
            Agent: "Agent A",
            Model: "model-a",
            TurnNumber: turnNumber,
            10,
            5,
            0,
            0,
            0,
            0,
            0,
            0,
            Timestamp: "10:00:00",
            RoutingSteps: [],
            RequestSummary: requestSummary,
            ResponseSummary: responseSummary);
    }

    private static Conversation MakeConversation(params ConversationTurn[] turns)
    {
        return new Conversation(
            Id: "sess-1",
            Title: "Test Conversation",
            FirstTimestamp: "10:00:00",
            LastTimestamp: "10:05:00",
            0,
            0,
            0,
            false,
            Turns: turns);
    }

    [Fact]
    public void Renders_request_and_response_bubbles_for_each_turn()
    {
        using var ctx = new BunitContext();

        var conversation = MakeConversation(
            MakeTurn(1, requestSummary: "What broke?", responseSummary: "The cache went stale."));

        var cut = ctx.Render<SessionConversationPane>(p =>
            p.Add(parameterSelector: c => c.Conversation, value: conversation));

        cut.Markup.Should().Contain("What broke?");
        cut.Markup.Should().Contain("The cache went stale.");
        cut.Markup.Should().Contain("Turn 1");
    }

    [Fact]
    public void Renders_turns_in_ascending_turn_number_order_regardless_of_input_order()
    {
        using var ctx = new BunitContext();

        var conversation = MakeConversation(
            MakeTurn(2, requestSummary: "Second request", responseSummary: "Second response"),
            MakeTurn(1, requestSummary: "First request", responseSummary: "First response"));

        var cut = ctx.Render<SessionConversationPane>(p =>
            p.Add(parameterSelector: c => c.Conversation, value: conversation));

        var firstIndex = cut.Markup.IndexOf(value: "First request", comparisonType: StringComparison.Ordinal);
        var secondIndex = cut.Markup.IndexOf(value: "Second request", comparisonType: StringComparison.Ordinal);

        firstIndex.Should().BeGreaterThan(-1);
        secondIndex.Should().BeGreaterThan(-1);
        firstIndex.Should().BeLessThan(secondIndex);
    }

    [Fact]
    public void Shows_muted_placeholders_when_a_turn_has_no_request_or_response_summary()
    {
        using var ctx = new BunitContext();

        var conversation = MakeConversation(MakeTurn(1, null, null));

        var cut = ctx.Render<SessionConversationPane>(p =>
            p.Add(parameterSelector: c => c.Conversation, value: conversation));

        cut.Markup.Should().Contain("No request captured");
        cut.Markup.Should().Contain("No response captured");
    }
}