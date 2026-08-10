using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using Bunit;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="SettingsModal"/>: the telemetry address field, the typed-confirmation gate on
/// the destructive Reset/Purge actions and what they actually clear, and the close callbacks.
/// </summary>
public sealed class SettingsModalTests
{
    private static Bunit.BunitContext NewContext(out LiveDataStore liveDataStore, out IGuiSettingsStore settingsStore)
    {
        var ctx = new Bunit.BunitContext();
        liveDataStore = new LiveDataStore(serverAddress: "https://127.0.0.1:59996");
        settingsStore = new GuiSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        ctx.Services.AddSingleton(liveDataStore);
        ctx.Services.AddSingleton(settingsStore);
        return ctx;
    }

    [Fact]
    public void Renders_the_two_destructive_action_buttons_initially()
    {
        using var ctx = NewContext(out _, out _);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Reset Stats");
        cut.Markup.Should().Contain("Clear History");
    }

    [Fact]
    public void Renders_the_persisted_telemetry_address()
    {
        using var ctx = NewContext(out _, out var settingsStore);
        settingsStore.Save(new GuiSettings("https://example.test:9999"));

        var cut = ctx.Render<SettingsModal>();

        cut.Find("#telemetry-address").GetAttribute("value").Should().Be("https://example.test:9999");
    }

    [Fact]
    public void Saving_the_telemetry_address_persists_it_and_shows_a_confirmation()
    {
        using var ctx = NewContext(out _, out var settingsStore);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("#telemetry-address").Input("https://example.test:7777");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        settingsStore.Load().TelemetryServerAddress.Should().Be("https://example.test:7777");
        cut.Markup.Should().Contain("Restart the app");
    }

    [Fact]
    public void Clicking_the_backdrop_invokes_OnClose()
    {
        using var ctx = NewContext(out _, out _);
        var closed = false;

        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.OnClose, () => closed = true));
        cut.Find("div").Click();

        closed.Should().BeTrue();
    }

    [Fact]
    public void Clicking_the_close_x_invokes_OnClose()
    {
        using var ctx = NewContext(out _, out _);
        var closed = false;

        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.OnClose, () => closed = true));
        cut.FindAll("button").First().Click();

        closed.Should().BeTrue();
    }

    [Fact]
    public void Starting_reset_shows_the_confirmation_prompt_requiring_RESET()
    {
        using var ctx = NewContext(out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();

        cut.Markup.Should().Contain("RESET");
        var confirm = cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset"));
        confirm.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Typing_the_exact_confirmation_word_enables_the_confirm_button()
    {
        using var ctx = NewContext(out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("id") != "telemetry-address").Input("RESET");

        var confirm = cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset"));
        confirm.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Confirming_a_reset_invokes_OnClose_and_clears_live_events_but_not_log_lines()
    {
        using var ctx = NewContext(out var liveDataStore, out _);
        var closed = false;

        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.OnClose, () => closed = true));
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("id") != "telemetry-address").Input("RESET");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset")).Click();

        closed.Should().BeTrue();
        liveDataStore.Conversations.Should().BeEmpty();
    }

    [Fact]
    public void Confirming_a_purge_clears_both_live_events_and_log_lines()
    {
        using var ctx = NewContext(out var liveDataStore, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear History")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("id") != "telemetry-address").Input("PURGE");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).Click();

        liveDataStore.Conversations.Should().BeEmpty();
        liveDataStore.LogLines.Should().BeEmpty();
    }

    [Fact]
    public void Clicking_cancel_returns_to_the_initial_two_button_view()
    {
        using var ctx = NewContext(out _, out _);

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
        using var ctx = NewContext(out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear History")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("id") != "telemetry-address").Input("RESET");

        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).HasAttribute("disabled").Should().BeTrue();

        cut.FindAll("input").First(i => i.GetAttribute("id") != "telemetry-address").Input("PURGE");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).HasAttribute("disabled").Should().BeFalse();
    }
}

