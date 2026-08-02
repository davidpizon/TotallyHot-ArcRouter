using TotallyHot.ArcRouter.Gui.Components;
using Bunit;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="SettingsModal"/>: the typed-confirmation gate on the destructive Reset/Purge
/// actions, and the close callbacks.
/// </summary>
public sealed class SettingsModalTests
{
    [Fact]
    public void Renders_the_two_destructive_action_buttons_initially()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Reset Stats");
        cut.Markup.Should().Contain("Clear History");
    }

    [Fact]
    public void Clicking_the_backdrop_invokes_OnClose()
    {
        using var ctx = new Bunit.BunitContext();
        var closed = false;

        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.OnClose, () => closed = true));
        cut.Find("div").Click();

        closed.Should().BeTrue();
    }

    [Fact]
    public void Clicking_the_close_x_invokes_OnClose()
    {
        using var ctx = new Bunit.BunitContext();
        var closed = false;

        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.OnClose, () => closed = true));
        cut.FindAll("button").First().Click();

        closed.Should().BeTrue();
    }

    [Fact]
    public void Starting_reset_shows_the_confirmation_prompt_requiring_RESET()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();

        cut.Markup.Should().Contain("RESET");
        var confirm = cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset"));
        confirm.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Typing_the_exact_confirmation_word_enables_the_confirm_button()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();
        cut.Find("input").Input("RESET");

        var confirm = cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset"));
        confirm.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Confirming_a_reset_invokes_OnClose()
    {
        using var ctx = new Bunit.BunitContext();
        var closed = false;

        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.OnClose, () => closed = true));
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();
        cut.Find("input").Input("RESET");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset")).Click();

        closed.Should().BeTrue();
    }

    [Fact]
    public void Clicking_cancel_returns_to_the_initial_two_button_view()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear History")).Click();
        cut.Markup.Should().Contain("PURGE");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();

        cut.Markup.Should().Contain("Clear History");
        cut.Markup.Should().NotContain("PURGE");
    }

    [Fact]
    public void Purge_requires_the_word_PURGE_not_RESET()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear History")).Click();
        cut.Find("input").Input("RESET");

        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).HasAttribute("disabled").Should().BeTrue();

        cut.Find("input").Input("PURGE");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).HasAttribute("disabled").Should().BeFalse();
    }
}

