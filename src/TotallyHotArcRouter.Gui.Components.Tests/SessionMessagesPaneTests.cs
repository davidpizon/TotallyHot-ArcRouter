using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.JSInterop;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>bUnit coverage for <see cref="SessionMessagesPane"/> (#176 §12 Phase 1).</summary>
public sealed class SessionMessagesPaneTests
{
    private static Conversation MakeConversation(int turns = 1, string? request = "hello", string? response = "world")
    {
        var list = new List<ConversationTurn>();
        for (var i = 1; i <= turns; i++)
        {
            var at = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero).AddMinutes(i);
            list.Add(new ConversationTurn(
                Id: $"s1-t{i}", Agent: "sonnet", Model: "claude-sonnet", i,
                10, 20, 0, 0.01m, 0, 0, 100, 0,
                Timestamp: at.ToString("HH:mm:ss", CultureInfo.InvariantCulture), RoutingSteps: [],
                RequestSummary: request, ResponseSummary: response,
                TimestampUtc: at,
                RequestedModel: "auto", RoutedModel: "claude-sonnet",
                IsFallback: i == 1, SubstitutionReason: i == 1 ? "ForcedModel" : null,
                IsLive: true));
        }

        return new Conversation("s1", "Session", "10:01:00", "10:05:00", 0.01m * turns, 10 * turns, 20 * turns,
            HasFallbackTurns: true, Turns: list, IsLive: true);
    }

    private static BunitContext NewContext(SessionsViewState? state = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(state ?? new SessionsViewState());
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddSingleton<IClipboardService>(new FakeClipboardService());
        return ctx;
    }

    [Fact]
    public void Renders_two_rows_per_turn_in_turn_order()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation(2)));

        var rows = cut.FindAll(".ls-msg");
        rows.Should().HaveCount(4);
        rows[0].TextContent.Should().Contain("Client");
        rows[0].TextContent.Should().Contain("#1");
        rows[1].TextContent.Should().Contain("claude-sonnet");
    }

    [Fact]
    public void Request_row_shows_Client_with_requested_model_tooltip()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation()));

        cut.Find(".ls-msg-request .ls-msg-sender").GetAttribute("data-tip")
            .Should().Contain("Requested model: auto");
    }

    [Fact]
    public void Shows_placeholders_when_text_is_missing()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation(request: null, response: null)));

        cut.Markup.Should().Contain("No request captured");
        cut.Markup.Should().Contain("No response captured");
    }

    [Fact]
    public void Row_click_selects_turn_and_side()
    {
        var state = new SessionsViewState();
        using var ctx = NewContext(state);
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation()));

        cut.FindAll(".ls-msg-select")[0].Click();

        state.MessageSelection.Should().NotBeNull();
        state.MessageSelection!.TurnNumber.Should().Be(1);
        state.MessageSelection.Side.Should().Be(MessageSide.Request);
        cut.FindAll(".ls-msg-select")[0].GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void Bodies_are_unclamped_until_overflow_is_reported()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation(request: new string('x', 500))));

        cut.FindAll(".ls-msg-clamp").Should().BeEmpty();
        cut.FindAll(".ls-msg-more").Should().BeEmpty();
    }

    [Fact]
    public async Task Overflow_callback_renders_clamp_and_Show_more_together()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation(request: new string('x', 500))));

        await cut.InvokeAsync(() => cut.Instance.OnOverflowKeysChanged(["1:req"]));

        cut.Find(".ls-msg-request .ls-msg-body").ClassList.Should().Contain("ls-msg-clamp");
        cut.Find(".ls-msg-more").TextContent.Should().Contain("Show more");
    }

    [Fact]
    public void Chips_for_fallback_and_substitution_appear_on_response_rows()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation()));

        cut.Find(".ls-msg-response").TextContent.Should().Contain("fallback");
        cut.Find(".ls-msg-response").TextContent.Should().Contain("substituted");
    }

    [Fact]
    public void Newest_response_row_is_implicitly_selected()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation(2)));

        var pressed = cut.FindAll(".ls-msg-select[aria-pressed='true']");
        pressed.Should().ContainSingle();
        pressed[0].TextContent.Should().Contain("claude-sonnet");
        pressed[0].GetAttribute("aria-label").Should().Contain("turn 2");
    }

    [Fact]
    public void Show_earlier_turns_paging_reveals_older_turns()
    {
        using var ctx = NewContext();
        var many = MakeConversation(250);
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, many));

        cut.FindAll(".ls-msg").Count.Should().Be(400); // 200 turns × 2
        cut.Find(".ls-show-earlier").Click();
        cut.FindAll(".ls-msg").Count.Should().Be(500); // all 250 × 2
    }

    [Fact]
    public async Task Disposing_the_pane_calls_chatScroll_dispose()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation()));

        await cut.Instance.DisposeAsync();

        ctx.JSInterop.VerifyInvoke("chatScroll.dispose");
    }

    [Fact]
    public async Task Dispose_tolerates_a_disconnected_runtime()
    {
        await using var ctx = NewContext();
        ctx.JSInterop.SetupVoid("chatScroll.dispose", _ => true)
            .SetException(new JSDisconnectedException("gone"));

        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation()));

        var act = async () => await cut.Instance.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void In_session_filter_counts_loaded_rows_and_notes_earlier_turns()
    {
        var state = new SessionsViewState { InSessionSearchOpen = true, InSessionFilter = "hello" };
        using var ctx = NewContext(state);
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, MakeConversation(250)));

        // Window holds 200 turns × 2 rows = 400 loaded; "hello" hits only the request rows → 200 matches.
        cut.Markup.Should().Contain("200 matches in 400 loaded messages");
        cut.Markup.Should().Contain("Earlier turns aren't searched");
    }

    [Fact]
    public void Remounting_the_same_session_preserves_message_selection()
    {
        var state = new SessionsViewState
        {
            MessageSelection = new MessageSelection("s1", 1, MessageSide.Request)
        };
        using var ctx = NewContext(state);
        var conversation = MakeConversation(2);

        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, conversation));
        cut.Dispose();

        var cut2 = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, conversation));

        state.MessageSelection.Should().NotBeNull();
        state.MessageSelection!.TurnNumber.Should().Be(1);
        state.MessageSelection.Side.Should().Be(MessageSide.Request);
        cut2.FindAll(".ls-msg-select")[0].GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void Newly_appended_live_turns_receive_the_row_enter_append_class()
    {
        using var ctx = NewContext();
        var first = MakeConversation(1);
        var cut = ctx.Render<SessionMessagesPane>(p => p
            .Add(c => c.Conversation, first));

        cut.FindAll(".row-enter-append").Should().BeEmpty();

        var grown = MakeConversation(2);
        cut.Render(p => p.Add(c => c.Conversation, grown));

        cut.FindAll(".row-enter-append").Should().HaveCount(2); // request + response for turn 2
    }
}
