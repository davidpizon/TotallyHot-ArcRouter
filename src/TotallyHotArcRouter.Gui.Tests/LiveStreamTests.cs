using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="LiveStream"/> (the Sessions tab): the full-width oldest-first card
/// list, search filtering, and the double-click split view (session details + conversation
/// reproduction) with its back button. JSInterop is Loose since splitPane.init is a cosmetic
/// drag-divider hook this project has no JS engine to run.
/// </summary>
public sealed class LiveStreamTests
{
    private static Conversation MakeConversation(
        string id,
        string title,
        string agent = "Agent A",
        DateTimeOffset? timestampUtc = null,
        string? requestSummary = null,
        string? responseSummary = null)
    {
        return new Conversation(
            Id: id,
            Title: title,
            FirstTimestamp: "10:00:00",
            LastTimestamp: "10:05:00",
            0.01m,
            100,
            50,
            false,
            Turns:
            [
                new ConversationTurn(Id: $"{id}-t1", Agent: agent, Model: "model-a", 1,
                    100, 50, 0, 0.01m,
                    0, 0, 100, 0,
                    Timestamp: "10:00:00", RoutingSteps: [], RequestSummary: requestSummary,
                    ResponseSummary: responseSummary,
                    TimestampUtc: timestampUtc ?? DateTimeOffset.MinValue)
            ]);
    }

    [Fact]
    public void Shows_the_empty_state_when_there_are_no_conversations()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LiveStream>(p => p
            .Add(parameterSelector: c => c.Conversations, value: Array.Empty<Conversation>())
            .Add(parameterSelector: c => c.SelectedId, value: string.Empty));

        cut.Markup.Should().Contain("No conversations yet.");
    }

    [Fact]
    public void Shows_the_full_width_card_list_with_no_session_opened_by_default()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var conversations = new[]
            { MakeConversation(id: "s1", title: "First"), MakeConversation(id: "s2", title: "Second") };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(parameterSelector: c => c.Conversations, value: conversations)
            .Add(parameterSelector: c => c.SelectedId, value: string.Empty));

        cut.Markup.Should().Contain("First");
        cut.Markup.Should().Contain("Second");
        cut.Markup.Should().NotContain("Back to Sessions");
    }

    [Fact]
    public void Sorts_the_card_list_oldest_first()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var conversations = new[]
        {
            MakeConversation(id: "s1", title: "Newer",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 12, 0, 0, offset: TimeSpan.Zero)),
            MakeConversation(id: "s2", title: "Older",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 8, 0, 0, offset: TimeSpan.Zero))
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(parameterSelector: c => c.Conversations, value: conversations)
            .Add(parameterSelector: c => c.SelectedId, value: string.Empty));

        var cardTitles = cut.FindAll("button")
            .Select(b => b.TextContent)
            .Where(t => t.Contains(value: "Older", comparisonType: StringComparison.Ordinal) ||
                        t.Contains(value: "Newer", comparisonType: StringComparison.Ordinal))
            .ToList();

        cardTitles.FindIndex(t => t.Contains(value: "Older", comparisonType: StringComparison.Ordinal))
            .Should().BeLessThan(cardTitles.FindIndex(t =>
                t.Contains(value: "Newer", comparisonType: StringComparison.Ordinal)));
    }

    [Fact]
    public void Clicking_a_conversation_card_invokes_OnSelect_without_opening_the_split_view()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        string? selected = null;

        var conversations = new[]
            { MakeConversation(id: "s1", title: "First"), MakeConversation(id: "s2", title: "Second") };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(parameterSelector: c => c.Conversations, value: conversations)
            .Add(parameterSelector: c => c.SelectedId, value: "s1")
            .Add(parameterSelector: c => c.OnSelect, callback: id => selected = id));

        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Second", comparisonType: StringComparison.Ordinal)).Click();

        selected.Should().Be("s2");
        cut.Markup.Should().NotContain("Back to Sessions");
    }

    [Fact]
    public void Double_clicking_a_card_opens_the_split_view_and_invokes_OnSelect()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        string? selected = null;

        var conversations = new[]
        {
            MakeConversation(id: "s1", title: "First"),
            MakeConversation(id: "s2", title: "Second", requestSummary: "What is the root cause?",
                responseSummary: "It's a stale cache.")
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(parameterSelector: c => c.Conversations, value: conversations)
            .Add(parameterSelector: c => c.SelectedId, value: string.Empty)
            .Add(parameterSelector: c => c.OnSelect, callback: id => selected = id));

        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Second", comparisonType: StringComparison.Ordinal))
            .DoubleClick();

        selected.Should().Be("s2");
        cut.Markup.Should().Contain("Back to Sessions");
        cut.Markup.Should().Contain("Second");
        cut.Markup.Should().Contain("What is the root cause?");
        cut.Markup.Should().Contain("It's a stale cache.");
        // The full-width card list (including the other session's card) is hidden while the split view is open.
        cut.Markup.Should().NotContain("First");
    }

    [Fact]
    public void Clicking_the_back_button_collapses_the_split_view_back_to_the_card_list()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var conversations = new[]
            { MakeConversation(id: "s1", title: "First"), MakeConversation(id: "s2", title: "Second") };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(parameterSelector: c => c.Conversations, value: conversations)
            .Add(parameterSelector: c => c.SelectedId, value: string.Empty));

        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Second", comparisonType: StringComparison.Ordinal))
            .DoubleClick();
        cut.Markup.Should().Contain("Back to Sessions");

        cut.FindAll("button").First(b =>
            b.TextContent.Contains(value: "Back to Sessions", comparisonType: StringComparison.Ordinal)).Click();

        cut.Markup.Should().NotContain("Back to Sessions");
        cut.Markup.Should().Contain("First");
        cut.Markup.Should().Contain("Second");
    }

    [Fact]
    public void Typing_a_search_term_filters_the_conversation_list()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var conversations = new[]
        {
            MakeConversation(id: "s1", title: "First", agent: "Alpha"),
            MakeConversation(id: "s2", title: "Second", agent: "Beta")
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(parameterSelector: c => c.Conversations, value: conversations)
            .Add(parameterSelector: c => c.SelectedId, value: "s1"));

        cut.Find("input").Input("Second");

        var cardButtons = cut.FindAll("button").Select(b => b.TextContent).ToList();
        cardButtons.Should().Contain(t => t.Contains("Second", StringComparison.Ordinal));
        cardButtons.Should().NotContain(t => t.Contains("First", StringComparison.Ordinal));
    }
}