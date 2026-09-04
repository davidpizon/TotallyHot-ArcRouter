using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="TurnCard"/>: header rendering, expand/collapse toggle, and the
/// routing-decision/request/response detail panel it reveals.
/// </summary>
public sealed class TurnCardTests
{
    private static ConversationTurn MakeTurn(
        bool isFallback = false,
        string? requestSummary = "Please review this diff for security issues in the auth flow",
        string? responseSummary = "Found one issue.",
        IReadOnlyList<RoutingStep>? steps = null,
        string? requestedModel = null,
        string? routedModel = null,
        string? substitutionReason = null)
    {
        return new ConversationTurn(
            Id: "sess-1-t1",
            Agent: "Code Review Bot",
            Model: "claude-3-haiku",
            1,
            2000,
            800,
            85.5m,
            0.0063m,
            2,
            72m,
            245,
            26.2m,
            Timestamp: "14:15:32",
            RoutingSteps: steps ??
            [
                new RoutingStep(Status: StepStatus.Ok, Message: "History carried forward"),
                new RoutingStep(Status: StepStatus.Warn, Message: "Prompt growth trending up"),
                new RoutingStep(Status: StepStatus.Info, Message: "Route Confirmed: claude-3-haiku")
            ],
            RequestSummary: requestSummary,
            ResponseSummary: responseSummary,
            IsFallback: isFallback,
            RequestedModel: requestedModel,
            RoutedModel: routedModel,
            SubstitutionReason: substitutionReason);
    }

    [Fact]
    public void Renders_turn_number_agent_and_model()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<TurnCard>(p => p
            .Add(parameterSelector: c => c.Turn, value: MakeTurn())
            .Add(parameterSelector: c => c.TotalTurns, 4));

        cut.Markup.Should().Contain("1/4");
        cut.Markup.Should().Contain("Code Review Bot");
        cut.Markup.Should().Contain("claude-3-haiku");
    }

    [Fact]
    public void Shows_the_fallback_badge_only_when_the_turn_used_fallback_routing()
    {
        using var ctx = new BunitContext();

        var fallback = ctx.Render<TurnCard>(p =>
            p.Add(parameterSelector: c => c.Turn, value: MakeTurn(isFallback: true))
                .Add(parameterSelector: c => c.TotalTurns, 1));
        fallback.Markup.Should().Contain("fallback routing");

        var normal = ctx.Render<TurnCard>(p =>
            p.Add(parameterSelector: c => c.Turn, value: MakeTurn(isFallback: false))
                .Add(parameterSelector: c => c.TotalTurns, 1));
        normal.Markup.Should().NotContain("This turn was served by fallback routing");
    }

    [Fact]
    public void Marks_the_model_stat_and_accessible_label_when_the_router_substituted_a_model()
    {
        using var ctx = new BunitContext();
        var turn = MakeTurn(requestedModel: "gpt-4o", routedModel: "claude-3-haiku", substitutionReason: "CircuitOpen");

        var cut = ctx.Render<TurnCard>(p =>
            p.Add(parameterSelector: c => c.Turn, value: turn).Add(parameterSelector: c => c.TotalTurns, 1));

        cut.Find("button[aria-label]").GetAttribute("aria-label")
            .Should().Contain("requested gpt-4o, routed to claude-3-haiku");
    }

    [Fact]
    public void Does_not_mark_the_accessible_label_for_an_auto_select_or_no_substitution()
    {
        using var ctx = new BunitContext();

        var autoSelect = ctx.Render<TurnCard>(p => p.Add(parameterSelector: c => c.Turn,
                value: MakeTurn(requestedModel: "auto", routedModel: "claude-3-haiku",
                    substitutionReason: "AutoSelect"))
            .Add(parameterSelector: c => c.TotalTurns, 1));
        autoSelect.Find("button[aria-label]").GetAttribute("aria-label").Should().NotContain("requested");

        var none = ctx.Render<TurnCard>(p => p.Add(parameterSelector: c => c.Turn,
            value: MakeTurn(requestedModel: "claude-3-haiku", routedModel: "claude-3-haiku",
                substitutionReason: "None")).Add(parameterSelector: c => c.TotalTurns, 1));
        none.Find("button[aria-label]").GetAttribute("aria-label").Should().NotContain("requested");
    }

    [Fact]
    public void Title_excerpt_falls_back_to_turn_number_when_no_request_summary()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<TurnCard>(p =>
            p.Add(parameterSelector: c => c.Turn, value: MakeTurn(requestSummary: null))
                .Add(parameterSelector: c => c.TotalTurns, 1));

        cut.Markup.Should().Contain("Turn 1");
    }

    [Fact]
    public void Title_excerpt_truncates_long_requests_to_eight_words()
    {
        using var ctx = new BunitContext();
        var longRequest = "one two three four five six seven eight nine ten";

        var cut = ctx.Render<TurnCard>(p =>
            p.Add(parameterSelector: c => c.Turn, value: MakeTurn(requestSummary: longRequest))
                .Add(parameterSelector: c => c.TotalTurns, 1));

        cut.Markup.Should().Contain("one two three four five six seven eight…");
        cut.Markup.Should().NotContain("nine");
    }

    [Fact]
    public void Clicking_the_header_expands_and_shows_the_routing_decision_log()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<TurnCard>(p =>
            p.Add(parameterSelector: c => c.Turn, value: MakeTurn()).Add(parameterSelector: c => c.TotalTurns, 1));

        cut.Markup.Should().NotContain("Routing Decision");

        cut.Find("button").Click();

        cut.Markup.Should().Contain("Routing Decision");
        cut.Markup.Should().Contain("History carried forward");
        cut.Markup.Should().Contain("Prompt growth trending up");
        cut.Markup.Should().Contain("Route Confirmed: claude-3-haiku");
        cut.Markup.Should().Contain("Request");
        cut.Markup.Should().Contain("Response");
    }

    [Fact]
    public void Clicking_the_header_twice_collapses_it_again()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<TurnCard>(p =>
            p.Add(parameterSelector: c => c.Turn, value: MakeTurn()).Add(parameterSelector: c => c.TotalTurns, 1));

        cut.Find("button").Click();
        cut.Find("button").Click();

        cut.Markup.Should().NotContain("Routing Decision");
    }

    [Fact]
    public void Ok_status_steps_render_when_expanded()
    {
        using var ctx = new BunitContext();
        var turn = MakeTurn(steps: [new RoutingStep(Status: StepStatus.Ok, Message: "All good")]);

        var cut = ctx.Render<TurnCard>(p =>
            p.Add(parameterSelector: c => c.Turn, value: turn).Add(parameterSelector: c => c.TotalTurns, 1));
        cut.Find("button").Click();

        cut.Markup.Should().Contain("All good");
    }

    [Fact]
    public void Zero_roi_and_zero_cache_hit_render_a_dash()
    {
        using var ctx = new BunitContext();
        var turn = MakeTurn() with { RoutingRoi = 0m, CacheHitRate = 0m };

        var cut = ctx.Render<TurnCard>(p =>
            p.Add(parameterSelector: c => c.Turn, value: turn).Add(parameterSelector: c => c.TotalTurns, 1));

        cut.Markup.Should().Contain("—");
    }
}