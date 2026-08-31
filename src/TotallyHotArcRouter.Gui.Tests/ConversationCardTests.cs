using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using Bunit;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Tests for <see cref="ConversationCard"/>: selection state, fallback badge, and click callback.</summary>
public sealed class ConversationCardTests
{
    private static Conversation MakeConversation(bool hasFallback = false, bool isUsedForTraining = false) => new(
        Id: "sess-1",
        Title: "Test Conversation",
        FirstTimestamp: "10:00:00",
        LastTimestamp: "10:05:00",
        TotalCost: 0.123456m,
        TotalPromptTokens: 1500,
        TotalCompletionTokens: 500,
        HasFallbackTurns: hasFallback,
        Turns:
        [
            new(Id: "sess-1-t1", Agent: "Agent A", Model: "model-a", TurnNumber: 1,
                PromptTokens: 1500, CompletionTokens: 500, RoutingRoi: 0, TotalCost: 0.1m,
                ToolExecutionSteps: 0, CacheHitRate: 0, TimeToFirstTokenMs: 100, ContextBufferPercent: 0,
                Timestamp: "10:00:00", RoutingSteps: []),
        ],
        IsUsedForTraining: isUsedForTraining);

    [Fact]
    public void Renders_the_conversation_title()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ConversationCard>(p => p.Add(c => c.Conversation, MakeConversation()));

        cut.Markup.Should().Contain("Test Conversation");
    }

    [Fact]
    public void Shows_the_fallback_badge_only_when_the_conversation_has_fallback_turns()
    {
        using var ctx = new Bunit.BunitContext();

        var withFallback = ctx.Render<ConversationCard>(p => p.Add(c => c.Conversation, MakeConversation(hasFallback: true)));
        withFallback.Markup.Should().Contain("⚠");

        var without = ctx.Render<ConversationCard>(p => p.Add(c => c.Conversation, MakeConversation(hasFallback: false)));
        without.Find("button").QuerySelector("[data-tip*='fallback routing']").Should().BeNull();
    }

    [Fact]
    public void Shows_the_training_badge_only_when_the_session_was_used_for_live_training()
    {
        using var ctx = new Bunit.BunitContext();

        var used = ctx.Render<ConversationCard>(p => p.Add(c => c.Conversation, MakeConversation(isUsedForTraining: true)));
        used.Find("button").QuerySelector("[data-tip*='live-learning corpus']").Should().NotBeNull();

        var notUsed = ctx.Render<ConversationCard>(p => p.Add(c => c.Conversation, MakeConversation(isUsedForTraining: false)));
        notUsed.Find("button").QuerySelector("[data-tip*='live-learning corpus']").Should().BeNull();
    }

    [Fact]
    public void Clicking_the_card_invokes_OnSelect_with_the_conversation_id()
    {
        using var ctx = new Bunit.BunitContext();
        string? selected = null;

        var cut = ctx.Render<ConversationCard>(p => p
            .Add(c => c.Conversation, MakeConversation())
            .Add(c => c.OnSelect, (string id) => selected = id));

        cut.Find("button").Click();

        selected.Should().Be("sess-1");
    }

    [Fact]
    public void Double_clicking_the_card_invokes_OnDoubleClick_with_the_conversation_id()
    {
        using var ctx = new Bunit.BunitContext();
        string? opened = null;

        var cut = ctx.Render<ConversationCard>(p => p
            .Add(c => c.Conversation, MakeConversation())
            .Add(c => c.OnDoubleClick, (string id) => opened = id));

        cut.Find("button").DoubleClick();

        opened.Should().Be("sess-1");
    }

    [Fact]
    public void Aria_pressed_reflects_the_IsSelected_parameter()
    {
        using var ctx = new Bunit.BunitContext();

        // Blazor renders a `bool`-valued attribute as a conditional attribute: present (empty value)
        // when true, omitted entirely when false - not the literal string "true"/"false".
        var selected = ctx.Render<ConversationCard>(p => p
            .Add(c => c.Conversation, MakeConversation())
            .Add(c => c.IsSelected, true));
        selected.Find("button").HasAttribute("aria-pressed").Should().BeTrue();

        var notSelected = ctx.Render<ConversationCard>(p => p
            .Add(c => c.Conversation, MakeConversation())
            .Add(c => c.IsSelected, false));
        notSelected.Find("button").HasAttribute("aria-pressed").Should().BeFalse();
    }

    [Fact]
    public void Shows_a_plus_count_when_there_are_more_than_two_distinct_agents()
    {
        using var ctx = new Bunit.BunitContext();
        var conversation = MakeConversation() with
        {
            Turns =
            [
                new(Id: "t1", Agent: "Agent A", Model: "m", TurnNumber: 1, PromptTokens: 1, CompletionTokens: 1,
                    RoutingRoi: 0, TotalCost: 0, ToolExecutionSteps: 0, CacheHitRate: 0, TimeToFirstTokenMs: 0,
                    ContextBufferPercent: 0, Timestamp: "t", RoutingSteps: []),
                new(Id: "t2", Agent: "Agent B", Model: "m", TurnNumber: 2, PromptTokens: 1, CompletionTokens: 1,
                    RoutingRoi: 0, TotalCost: 0, ToolExecutionSteps: 0, CacheHitRate: 0, TimeToFirstTokenMs: 0,
                    ContextBufferPercent: 0, Timestamp: "t", RoutingSteps: []),
                new(Id: "t3", Agent: "Agent C", Model: "m", TurnNumber: 3, PromptTokens: 1, CompletionTokens: 1,
                    RoutingRoi: 0, TotalCost: 0, ToolExecutionSteps: 0, CacheHitRate: 0, TimeToFirstTokenMs: 0,
                    ContextBufferPercent: 0, Timestamp: "t", RoutingSteps: []),
            ],
        };

        var cut = ctx.Render<ConversationCard>(p => p.Add(c => c.Conversation, conversation));

        cut.Markup.Should().Contain("+1");
    }
}

