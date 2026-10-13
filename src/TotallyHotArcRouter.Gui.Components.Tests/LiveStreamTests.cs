using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="LiveStream"/> (the Sessions tab): always-on three-pane layout, newest-first
/// rail, implicit versus explicit selection, and search. JSInterop is Loose since split-pane and
/// chat-scroll need a browser engine this project does not run.
/// </summary>
public sealed class LiveStreamTests
{
    private static Conversation MakeConversation(
        string id,
        string title,
        string agent = "Agent A",
        DateTimeOffset? timestampUtc = null,
        string? requestSummary = null,
        string? responseSummary = null,
        bool isLive = true)
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
                    TimestampUtc: timestampUtc ?? DateTimeOffset.MinValue,
                    IsLive: isLive)
            ],
            IsLive: isLive);
    }

    private static BunitContext NewContext(TimeProvider? time = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IClipboardService>(new FakeClipboardService());
        ctx.Services.AddSingleton(new SessionsViewState());
        ctx.Services.AddSingleton(time ?? TimeProvider.System);
        return ctx;
    }

    [Fact]
    public void Shows_the_empty_state_when_there_are_no_conversations()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [])
            .Add(c => c.SelectedId, string.Empty));

        cut.Markup.Should().Contain("No conversations yet.");
        cut.Find("[data-testid='client-drop-in-base-url']").GetAttribute("value")
            .Should().Be(OpenAiCompatibleDropIn.BaseUrl);
    }

    [Fact]
    public void Shows_the_history_notice_in_the_empty_state()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [])
            .Add(c => c.SelectedId, string.Empty)
            .Add(c => c.HistoryNotice, "Persisted history couldn't be loaded: boom"));

        cut.Find(".ls-history-notice").TextContent.Should().Be("Persisted history couldn't be loaded: boom");
    }

    [Fact]
    public void Renders_three_panes_when_conversations_exist()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [MakeConversation("s1", "First")])
            .Add(c => c.SelectedId, string.Empty));

        cut.Find(".ls-sessions-layout").Should().NotBeNull();
        cut.Find(".ls-sessions-rail").Should().NotBeNull();
        cut.Find(".ls-chat-pane").Should().NotBeNull();
        cut.Find(".ls-details-pane").Should().NotBeNull();
        cut.Markup.Should().NotContain("Back to Sessions");
    }

    [Fact]
    public void Sorts_the_rail_most_recently_active_first()
    {
        using var ctx = NewContext();

        var conversations = new[]
        {
            MakeConversation("s1", "Older",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero)),
            MakeConversation("s2", "Newer",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, string.Empty));

        var options = cut.FindAll("[role='option']").Select(o => o.TextContent).ToList();
        options.FindIndex(t => t.Contains("Newer", StringComparison.Ordinal))
            .Should().BeLessThan(options.FindIndex(t => t.Contains("Older", StringComparison.Ordinal)));
    }

    [Fact]
    public void Implicit_selection_is_the_newest_session_and_does_not_invoke_OnSelect()
    {
        using var ctx = NewContext();
        string? selected = null;

        var conversations = new[]
        {
            MakeConversation("s1", "Older",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero)),
            MakeConversation("s2", "Newer",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, string.Empty)
            .Add(c => c.OnSelect, id => selected = id));

        selected.Should().BeNull();
        cut.Find("[role='option'][aria-selected='true']").TextContent.Should().Contain("Newer");
        cut.Find(".ls-chat-title").TextContent.Should().Contain("Newer");
    }

    [Fact]
    public void Clicking_a_rail_item_invokes_OnSelect()
    {
        using var ctx = NewContext();
        string? selected = null;

        var conversations = new[]
        {
            MakeConversation("s1", "First",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)),
            MakeConversation("s2", "Second",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero))
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, "s1")
            .Add(c => c.OnSelect, id => selected = id));

        cut.FindAll("[role='option']")
            .First(b => b.TextContent.Contains("Second", StringComparison.Ordinal))
            .Click();

        selected.Should().Be("s2");
    }

    [Fact]
    public void Keyboard_Home_on_a_rail_item_invokes_OnSelect()
    {
        using var ctx = NewContext();
        string? selected = null;

        var conversations = new[]
        {
            MakeConversation("s1", "Newer",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)),
            MakeConversation("s2", "Older",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero))
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, "s1")
            .Add(c => c.OnSelect, id => selected = id));

        cut.Find("[role='option'][aria-selected='true']")
            .KeyDown(key: "End");

        selected.Should().Be("s2");
    }

    [Fact]
    public void Typing_a_search_term_filters_the_rail()
    {
        using var ctx = NewContext();

        var conversations = new[]
        {
            MakeConversation("s1", "First", agent: "Alpha"),
            MakeConversation("s2", "Second", agent: "Beta")
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, "s1"));

        cut.Find("input[aria-label='Search sessions']").Input("Second");

        var options = cut.FindAll("[role='option']").Select(o => o.TextContent).ToList();
        options.Should().Contain(t => t.Contains("Second", StringComparison.Ordinal));
        options.Should().NotContain(t => t.Contains("First", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_dot_class_is_idle_at_the_five_minute_boundary()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        using var ctx = NewContext(clock);

        var conversations = new[]
        {
            MakeConversation("s1", "Idle",
                timestampUtc: now.AddMinutes(-5), isLive: true)
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, "s1"));

        cut.Find(".ls-status-dot").ClassList.Should().Contain("ls-status-idle");
    }

    [Fact]
    public void Footer_shows_capture_off_when_disabled()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [MakeConversation("s1", "First")])
            .Add(c => c.SelectedId, string.Empty)
            .Add(c => c.TranscriptCaptureEnabled, false));

        cut.Find(".ls-rail-footer").TextContent.Should().Contain("Transcript capture off");
    }

    [Fact]
    public void Falls_back_when_the_selected_session_disappears()
    {
        using var ctx = NewContext();

        var conversations = new[]
        {
            MakeConversation("s1", "First",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)),
            MakeConversation("s2", "Second",
                timestampUtc: new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero))
        };

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.SelectedId, "missing"));

        cut.Find(".ls-chat-title").TextContent.Should().Contain("First");
    }

    [Fact]
    public void Shows_the_history_notice_above_the_panes()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [MakeConversation("s1", "First")])
            .Add(c => c.SelectedId, string.Empty)
            .Add(c => c.HistoryNotice, "Showing the newest 312 persisted turns."));

        cut.Find(".ls-history-notice").TextContent.Should().Be("Showing the newest 312 persisted turns.");
    }

    [Fact]
    public void Implicit_selection_reports_the_shown_session_once_without_selecting_it()
    {
        using var ctx = NewContext();
        var shown = new List<string>();
        var selected = new List<string>();

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [MakeConversation("s1", "First")])
            .Add(c => c.SelectedId, string.Empty)
            .Add(c => c.OnImplicitSessionShown, shown.Add)
            .Add(c => c.OnSelect, selected.Add));
        cut.Render(p => p.Add(c => c.Conversations, [MakeConversation("s1", "First")]));

        shown.Should().Equal("s1");
        selected.Should().BeEmpty();
    }

    [Fact]
    public void Explicit_selection_does_not_report_an_implicit_session()
    {
        using var ctx = NewContext();
        var shown = new List<string>();

        ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [MakeConversation("s1", "First")])
            .Add(c => c.SelectedId, "s1")
            .Add(c => c.OnImplicitSessionShown, shown.Add));

        shown.Should().BeEmpty();
    }
}
