using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using Bunit;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Tests for <see cref="ConversationSummary"/>: the pinned stat strip above the turn list.</summary>
public sealed class ConversationSummaryTests
{
    private static Conversation MakeConversation(IReadOnlyList<ConversationTurn>? turns = null) => new(
        Id: "sess-1",
        Title: "Summary Conversation",
        FirstTimestamp: "10:00:00",
        LastTimestamp: "10:05:00",
        TotalCost: 0.123456m,
        TotalPromptTokens: 1500,
        TotalCompletionTokens: 500,
        HasFallbackTurns: false,
        Turns: turns ??
        [
            new(Id: "t1", Agent: "Agent A", Model: "m", TurnNumber: 1, PromptTokens: 100, CompletionTokens: 50,
                RoutingRoi: 80m, TotalCost: 0.05m, ToolExecutionSteps: 1, CacheHitRate: 0, TimeToFirstTokenMs: 100,
                ContextBufferPercent: 10, Timestamp: "10:00:00", RoutingSteps: []),
        ]);

    [Fact]
    public void Renders_title_and_cost()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ConversationSummary>(p => p.Add(c => c.Conversation, MakeConversation()));

        cut.Markup.Should().Contain("Summary Conversation");
        cut.Markup.Should().Contain("0.123456");
    }

    [Fact]
    public void Shows_fallback_badge_when_conversation_has_fallback_turns()
    {
        using var ctx = new Bunit.BunitContext();
        var conversation = MakeConversation() with { HasFallbackTurns = true };

        var cut = ctx.Render<ConversationSummary>(p => p.Add(c => c.Conversation, conversation));

        cut.Markup.Should().Contain("Fallback");
    }

    [Fact]
    public void Shows_a_dash_for_average_roi_when_no_turns_have_positive_roi()
    {
        using var ctx = new Bunit.BunitContext();
        var conversation = MakeConversation([]);

        var cut = ctx.Render<ConversationSummary>(p => p.Add(c => c.Conversation, conversation));

        cut.Markup.Should().Contain("—");
    }

    [Fact]
    public void Renders_the_sparkline_when_there_are_turns()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ConversationSummary>(p => p.Add(c => c.Conversation, MakeConversation()));

        cut.Find("svg").Should().NotBeNull();
    }
}

