using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using Bunit;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Tests for <see cref="TurnCard"/>: header rendering, expand/collapse toggle, and the
/// routing-decision/request/response detail panel it reveals.</summary>
public sealed class TurnCardTests
{
    private static ConversationTurn MakeTurn(
        bool isFallback = false,
        string? requestSummary = "Please review this diff for security issues in the auth flow",
        string? responseSummary = "Found one issue.",
        IReadOnlyList<RoutingStep>? steps = null) => new(
        Id: "sess-1-t1",
        Agent: "Code Review Bot",
        Model: "claude-3-haiku",
        TurnNumber: 1,
        PromptTokens: 2000,
        CompletionTokens: 800,
        RoutingRoi: 85.5m,
        TotalCost: 0.0063m,
        ToolExecutionSteps: 2,
        CacheHitRate: 72m,
        TimeToFirstTokenMs: 245,
        ContextBufferPercent: 26.2m,
        Timestamp: "14:15:32",
        RoutingSteps: steps ??
        [
            new(StepStatus.Ok, "History carried forward"),
            new(StepStatus.Warn, "Prompt growth trending up"),
            new(StepStatus.Info, "Route Confirmed: claude-3-haiku"),
        ],
        RequestSummary: requestSummary,
        ResponseSummary: responseSummary,
        IsFallback: isFallback);

    [Fact]
    public void Renders_turn_number_agent_and_model()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<TurnCard>(p => p
            .Add(c => c.Turn, MakeTurn())
            .Add(c => c.TotalTurns, 4));

        cut.Markup.Should().Contain("1/4");
        cut.Markup.Should().Contain("Code Review Bot");
        cut.Markup.Should().Contain("claude-3-haiku");
    }

    [Fact]
    public void Shows_the_fallback_badge_only_when_the_turn_used_fallback_routing()
    {
        using var ctx = new Bunit.BunitContext();

        var fallback = ctx.Render<TurnCard>(p => p.Add(c => c.Turn, MakeTurn(isFallback: true)).Add(c => c.TotalTurns, 1));
        fallback.Markup.Should().Contain("fallback routing");

        var normal = ctx.Render<TurnCard>(p => p.Add(c => c.Turn, MakeTurn(isFallback: false)).Add(c => c.TotalTurns, 1));
        normal.Markup.Should().NotContain("This turn was served by fallback routing");
    }

    [Fact]
    public void Title_excerpt_falls_back_to_turn_number_when_no_request_summary()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<TurnCard>(p => p.Add(c => c.Turn, MakeTurn(requestSummary: null)).Add(c => c.TotalTurns, 1));

        cut.Markup.Should().Contain("Turn 1");
    }

    [Fact]
    public void Title_excerpt_truncates_long_requests_to_eight_words()
    {
        using var ctx = new Bunit.BunitContext();
        var longRequest = "one two three four five six seven eight nine ten";

        var cut = ctx.Render<TurnCard>(p => p.Add(c => c.Turn, MakeTurn(requestSummary: longRequest)).Add(c => c.TotalTurns, 1));

        cut.Markup.Should().Contain("one two three four five six seven eight…");
        cut.Markup.Should().NotContain("nine");
    }

    [Fact]
    public void Clicking_the_header_expands_and_shows_the_routing_decision_log()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<TurnCard>(p => p.Add(c => c.Turn, MakeTurn()).Add(c => c.TotalTurns, 1));

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
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<TurnCard>(p => p.Add(c => c.Turn, MakeTurn()).Add(c => c.TotalTurns, 1));

        cut.Find("button").Click();
        cut.Find("button").Click();

        cut.Markup.Should().NotContain("Routing Decision");
    }

    [Fact]
    public void Ok_status_steps_render_when_expanded()
    {
        using var ctx = new Bunit.BunitContext();
        var turn = MakeTurn(steps: [new RoutingStep(StepStatus.Ok, "All good")]);

        var cut = ctx.Render<TurnCard>(p => p.Add(c => c.Turn, turn).Add(c => c.TotalTurns, 1));
        cut.Find("button").Click();

        cut.Markup.Should().Contain("All good");
    }

    [Fact]
    public void Zero_roi_and_zero_cache_hit_render_a_dash()
    {
        using var ctx = new Bunit.BunitContext();
        var turn = MakeTurn() with { RoutingRoi = 0m, CacheHitRate = 0m };

        var cut = ctx.Render<TurnCard>(p => p.Add(c => c.Turn, turn).Add(c => c.TotalTurns, 1));

        cut.Markup.Should().Contain("—");
    }
}

