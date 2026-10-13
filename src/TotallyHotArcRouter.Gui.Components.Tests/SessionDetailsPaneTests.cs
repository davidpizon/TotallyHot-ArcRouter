using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>bUnit coverage for <see cref="SessionDetailsPane"/> and its tabs (#176 §12 Phase 1).</summary>
public sealed class SessionDetailsPaneTests
{
    private static Conversation MakeConversation(
        bool isLive = true,
        bool? synthesized = false,
        bool turnIsLive = true,
        int? promptTokens = 100,
        decimal? cost = 0.01m)
    {
        var turn = new ConversationTurn(
            Id: "s1-t1", Agent: "sonnet", Model: "claude-sonnet", 1,
            promptTokens, 50, 0, cost, 0, 0.5m, turnIsLive ? 120 : 0, 0,
            Timestamp: "10:00:00",
            RoutingSteps: [new RoutingStep(StepStatus.Warn, "fallback engaged")],
            RequestSummary: "req", ResponseSummary: "res",
            TimestampUtc: new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
            RequestedModel: "auto", RoutedModel: "claude-sonnet",
            IsLive: turnIsLive, Provider: turnIsLive ? "anthropic" : null);

        return new Conversation("s1", "Session", "10:00:00", "10:00:00", cost ?? 0m, promptTokens ?? 0, 50,
            false, [turn], UnpricedTurns: cost is null ? 1 : 0, IsLive: isLive,
            IsSessionSynthesized: synthesized);
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
    public void Tab_click_switches_the_active_panel()
    {
        var state = new SessionsViewState();
        using var ctx = NewContext(state);
        var cut = ctx.Render<SessionDetailsPane>(p => p
            .Add(c => c.Conversation, MakeConversation()));

        cut.FindAll("[role='tab']").First(t => t.TextContent.Contains("Session", StringComparison.Ordinal)).Click();

        state.DetailsTab.Should().Be(SessionsDetailsTab.Session);
        cut.Markup.Should().Contain("Models used");
    }

    [Fact]
    public void Arrow_keys_cycle_details_tabs()
    {
        var state = new SessionsViewState { DetailsTab = SessionsDetailsTab.Message };
        using var ctx = NewContext(state);
        var cut = ctx.Render<SessionDetailsPane>(p => p
            .Add(c => c.Conversation, MakeConversation()));

        cut.Find(".ls-details-tab.active").KeyDown("ArrowRight");

        state.DetailsTab.Should().Be(SessionsDetailsTab.Routing);
    }

    [Fact]
    public void Request_side_shows_prompt_fields()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<SessionDetailsPane>(p => p
            .Add(c => c.Conversation, MakeConversation())
            .Add(c => c.Selection, new MessageSelection("s1", 1, MessageSide.Request)));

        cut.Markup.Should().Contain("Request · Turn 1");
        cut.Markup.Should().Contain("Prompt tokens");
    }

    [Fact]
    public void Persisted_turn_shows_dash_for_live_only_fields()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<SessionDetailsPane>(p => p
            .Add(c => c.Conversation, MakeConversation(isLive: false, synthesized: null, turnIsLive: false))
            .Add(c => c.Selection, new MessageSelection("s1", 1, MessageSide.Response)));

        cut.Markup.Should().Contain("Persisted history does not record latency");
        cut.Markup.Should().NotContain(">120 ms<");
    }

    [Fact]
    public void Null_token_counts_render_as_dash_not_zero()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<SessionDetailsPane>(p => p
            .Add(c => c.Conversation, MakeConversation(promptTokens: null))
            .Add(c => c.Selection, new MessageSelection("s1", 1, MessageSide.Request)));

        cut.Markup.Should().Contain("not reported");
    }

    [Fact]
    public void Untracked_row_reads_unknown_for_persisted_only_sessions()
    {
        var state = new SessionsViewState { DetailsTab = SessionsDetailsTab.Session };
        using var ctx = NewContext(state);
        var cut = ctx.Render<SessionDetailsPane>(p => p
            .Add(c => c.Conversation, MakeConversation(isLive: false, synthesized: null, turnIsLive: false)));

        cut.Markup.Should().Contain("Unknown (not persisted)");
    }

    [Fact]
    public void Used_for_training_never_says_No_without_aggregate()
    {
        var state = new SessionsViewState { DetailsTab = SessionsDetailsTab.Session };
        using var ctx = NewContext(state);
        var cut = ctx.Render<SessionDetailsPane>(p => p
            .Add(c => c.Conversation, MakeConversation() with { IsUsedForTraining = false }));

        cut.Markup.Should().Contain("Used for training");
        cut.Markup.Should().Contain("Not in the loaded history");
        cut.Markup.Should().NotContain("Used for training</span><span>No</span>");
    }

    [Fact]
    public void Routing_steps_render_with_revived_tones()
    {
        var state = new SessionsViewState { DetailsTab = SessionsDetailsTab.Routing };
        using var ctx = NewContext(state);
        var cut = ctx.Render<SessionDetailsPane>(p => p
            .Add(c => c.Conversation, MakeConversation()));

        cut.Markup.Should().Contain("fallback engaged");
        cut.Find(".ds-step-warning").Should().NotBeNull();
    }

    [Fact]
    public void Session_totals_show_unpriced_floor()
    {
        var state = new SessionsViewState { DetailsTab = SessionsDetailsTab.Session };
        using var ctx = NewContext(state);
        var cut = ctx.Render<SessionDetailsPane>(p => p
            .Add(c => c.Conversation, MakeConversation(cost: null)));

        cut.Markup.Should().Contain("≥");
        cut.Markup.Should().Contain("unpriced");
    }
}
