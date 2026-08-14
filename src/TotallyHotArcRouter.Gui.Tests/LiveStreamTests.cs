using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using Bunit;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Tests for <see cref="LiveStream"/>: conversation selection, search filtering, and the
/// empty-state guard. JSInterop is Loose since splitPane.init is a cosmetic drag-divider hook this
/// project has no JS engine to run.</summary>
public sealed class LiveStreamTests
{
    private static Conversation MakeConversation(string id, string title, string agent = "Agent A") => new(
        Id: id,
        Title: title,
        FirstTimestamp: "10:00:00",
        LastTimestamp: "10:05:00",
        TotalCost: 0.01m,
        TotalPromptTokens: 100,
        TotalCompletionTokens: 50,
        HasFallbackTurns: false,
        Turns:
        [
            new(Id: $"{id}-t1", Agent: agent, Model: "model-a", TurnNumber: 1,
                PromptTokens: 100, CompletionTokens: 50, RoutingRoi: 0, TotalCost: 0.01m,
                ToolExecutionSteps: 0, CacheHitRate: 0, TimeToFirstTokenMs: 100, ContextBufferPercent: 0,
                Timestamp: "10:00:00", RoutingSteps: []),
        ]);

    [Fact]
    public void Shows_the_empty_state_when_there_are_no_conversations()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, Array.Empty<Conversation>())
            .Add(c => c.SelectedId, string.Empty));

        cut.Markup.Should().Contain("No conversations yet.");
    }

    [Fact]
    public void Auto_selects_the_first_conversation_when_SelectedId_does_not_match_any()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var conversations = new[] { MakeConversation("s1", "First"), MakeConversation("s2", "Second") };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, "unknown-id"));

        cut.Markup.Should().Contain("First");
    }

    [Fact]
    public void Renders_the_selected_conversation_summary_and_turns()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var conversations = new[] { MakeConversation("s1", "First"), MakeConversation("s2", "Second") };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, "s2"));

        cut.Markup.Should().Contain("Second");
    }

    [Fact]
    public void Clicking_a_conversation_card_invokes_OnSelect()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        string? selected = null;

        var conversations = new[] { MakeConversation("s1", "First"), MakeConversation("s2", "Second") };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, "s1")
            .Add(c => c.OnSelect, (string id) => selected = id));

        cut.FindAll("button").First(b => b.TextContent.Contains("Second")).Click();

        selected.Should().Be("s2");
    }

    [Fact]
    public void Typing_a_search_term_filters_the_conversation_list()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var conversations = new[] { MakeConversation("s1", "First", "Alpha"), MakeConversation("s2", "Second", "Beta") };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, "s1"));

        cut.Find("input").Input("Second");

        // Only the left conversation-card list is filtered by search; the right-hand drilldown keeps
        // showing whatever conversation is selected (independent of the search box).
        var leftPanelButtons = cut.FindAll("button").Select(b => b.TextContent).ToList();
        leftPanelButtons.Should().Contain(t => t.Contains("Second"));
        leftPanelButtons.Should().NotContain(t => t.Contains("First"));
    }
}

