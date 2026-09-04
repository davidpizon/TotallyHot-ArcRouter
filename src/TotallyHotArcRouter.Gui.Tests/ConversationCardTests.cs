using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Tests for <see cref="ConversationCard"/>: selection state, fallback badge, and click callback.</summary>
public sealed class ConversationCardTests
{
    private static Conversation MakeConversation(bool hasFallback = false, bool isUsedForTraining = false)
    {
        return new Conversation(
            Id: "sess-1",
            Title: "Test Conversation",
            FirstTimestamp: "10:00:00",
            LastTimestamp: "10:05:00",
            0.123456m,
            1500,
            500,
            HasFallbackTurns: hasFallback,
            Turns:
            [
                new ConversationTurn(Id: "sess-1-t1", Agent: "Agent A", Model: "model-a", 1,
                    1500, 500, 0, 0.1m,
                    0, 0, 100, 0,
                    Timestamp: "10:00:00", RoutingSteps: [])
            ],
            IsUsedForTraining: isUsedForTraining);
    }

    [Fact]
    public void Renders_the_conversation_title()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ConversationCard>(p =>
            p.Add(parameterSelector: c => c.Conversation, value: MakeConversation()));

        cut.Markup.Should().Contain("Test Conversation");
    }

    [Fact]
    public void Shows_the_fallback_badge_only_when_the_conversation_has_fallback_turns()
    {
        using var ctx = new BunitContext();

        var withFallback = ctx.Render<ConversationCard>(p =>
            p.Add(parameterSelector: c => c.Conversation, value: MakeConversation(hasFallback: true)));
        withFallback.Markup.Should().Contain("⚠");

        var without = ctx.Render<ConversationCard>(p =>
            p.Add(parameterSelector: c => c.Conversation, value: MakeConversation(hasFallback: false)));
        without.Find("button").QuerySelector("[data-tip*='fallback routing']").Should().BeNull();
    }

    [Fact]
    public void Shows_the_training_badge_only_when_the_session_was_used_for_live_training()
    {
        using var ctx = new BunitContext();

        var used = ctx.Render<ConversationCard>(p =>
            p.Add(parameterSelector: c => c.Conversation, value: MakeConversation(isUsedForTraining: true)));
        used.Find("button").QuerySelector("[data-tip*='live-learning corpus']").Should().NotBeNull();

        var notUsed = ctx.Render<ConversationCard>(p =>
            p.Add(parameterSelector: c => c.Conversation, value: MakeConversation(isUsedForTraining: false)));
        notUsed.Find("button").QuerySelector("[data-tip*='live-learning corpus']").Should().BeNull();
    }

    [Fact]
    public void Clicking_the_card_invokes_OnSelect_with_the_conversation_id()
    {
        using var ctx = new BunitContext();
        string? selected = null;

        var cut = ctx.Render<ConversationCard>(p => p
            .Add(parameterSelector: c => c.Conversation, value: MakeConversation())
            .Add(parameterSelector: c => c.OnSelect, callback: id => selected = id));

        cut.Find("button").Click();

        selected.Should().Be("sess-1");
    }

    [Fact]
    public void Double_clicking_the_card_invokes_OnDoubleClick_with_the_conversation_id()
    {
        using var ctx = new BunitContext();
        string? opened = null;

        var cut = ctx.Render<ConversationCard>(p => p
            .Add(parameterSelector: c => c.Conversation, value: MakeConversation())
            .Add(parameterSelector: c => c.OnDoubleClick, callback: id => opened = id));

        cut.Find("button").DoubleClick();

        opened.Should().Be("sess-1");
    }

    [Fact]
    public void Aria_pressed_reflects_the_IsSelected_parameter()
    {
        using var ctx = new BunitContext();

        // Blazor renders a `bool`-valued attribute as a conditional attribute: present (empty value)
        // when true, omitted entirely when false - not the literal string "true"/"false".
        var selected = ctx.Render<ConversationCard>(p => p
            .Add(parameterSelector: c => c.Conversation, value: MakeConversation())
            .Add(parameterSelector: c => c.IsSelected, true));
        selected.Find("button").HasAttribute("aria-pressed").Should().BeTrue();

        var notSelected = ctx.Render<ConversationCard>(p => p
            .Add(parameterSelector: c => c.Conversation, value: MakeConversation())
            .Add(parameterSelector: c => c.IsSelected, false));
        notSelected.Find("button").HasAttribute("aria-pressed").Should().BeFalse();
    }

    [Fact]
    public void Shows_a_plus_count_when_there_are_more_than_two_distinct_agents()
    {
        using var ctx = new BunitContext();
        var conversation = MakeConversation() with
        {
            Turns =
            [
                new ConversationTurn(Id: "t1", Agent: "Agent A", Model: "m", 1, 1, 1,
                    0, 0, 0, 0, 0,
                    0, Timestamp: "t", RoutingSteps: []),
                new ConversationTurn(Id: "t2", Agent: "Agent B", Model: "m", 2, 1, 1,
                    0, 0, 0, 0, 0,
                    0, Timestamp: "t", RoutingSteps: []),
                new ConversationTurn(Id: "t3", Agent: "Agent C", Model: "m", 3, 1, 1,
                    0, 0, 0, 0, 0,
                    0, Timestamp: "t", RoutingSteps: [])
            ]
        };

        var cut = ctx.Render<ConversationCard>(p => p.Add(parameterSelector: c => c.Conversation, value: conversation));

        cut.Markup.Should().Contain("+1");
    }
}