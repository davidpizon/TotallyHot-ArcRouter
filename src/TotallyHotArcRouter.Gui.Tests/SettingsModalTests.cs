using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Bunit;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="SettingsModal"/>: the telemetry address field, the Adaptive Routing toggle and
/// Sample Size input (docs/router/self-organizing-classification-plan.md Phase T6), the typed-confirmation
/// gate on the destructive Reset/Purge actions and what they actually clear, and the close callbacks.
/// </summary>
public sealed class SettingsModalTests
{
    private static Bunit.BunitContext NewContext(
        out LiveDataStore liveDataStore,
        out IGuiSettingsStore settingsStore,
        out RouterSettingsAdminStore routerSettingsStore,
        FakeRouterSettingsAdminClient? routerSettingsClient = null)
    {
        var ctx = new Bunit.BunitContext();
        liveDataStore = new LiveDataStore(serverAddress: "https://127.0.0.1:59996");
        var settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        settingsStore = new GuiSettingsStore(settingsPath);
        routerSettingsStore = new RouterSettingsAdminStore(routerSettingsClient ?? new FakeRouterSettingsAdminClient());
        ctx.Services.AddSingleton(liveDataStore);
        ctx.Services.AddSingleton(settingsStore);
        ctx.Services.AddSingleton(routerSettingsStore);
        ctx.Services.AddSingleton(_ => new TempFileCleanup(settingsPath));
        ctx.Services.GetRequiredService<TempFileCleanup>();
        return ctx;
    }

    /// <summary>A controllable <see cref="IRouterSettingsAdminClient"/> double, mirroring the router-side default (adaptive routing off, capacity 20000).</summary>
    private sealed class FakeRouterSettingsAdminClient : IRouterSettingsAdminClient
    {
        public RouterSettingsInfo Settings { get; set; } = new(AdaptiveRoutingEnabled: false, EmbeddingMemoryCapacity: 20_000);

        public RouterSettingsAdminException? Failure { get; set; }

        public Task<RouterSettingsInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult(Settings) : Task.FromException<RouterSettingsInfo>(Failure);

        public Task<RouterSettingsInfo> UpdateAsync(bool adaptiveRoutingEnabled, int embeddingMemoryCapacity, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                return Task.FromException<RouterSettingsInfo>(Failure);
            }

            Settings = new RouterSettingsInfo(adaptiveRoutingEnabled, embeddingMemoryCapacity);
            return Task.FromResult(Settings);
        }
    }

    [Fact]
    public void Renders_the_two_destructive_action_buttons_initially()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Reset Stats");
        cut.Markup.Should().Contain("Clear History");
    }

    [Fact]
    public void Renders_the_persisted_telemetry_address()
    {
        using var ctx = NewContext(out _, out var settingsStore, out _);
        settingsStore.Save(new GuiSettings("https://example.test:9999"));

        var cut = ctx.Render<SettingsModal>();

        cut.Find("#telemetry-address").GetAttribute("value").Should().Be("https://example.test:9999");
    }

    [Fact]
    public void Saving_the_telemetry_address_persists_it_and_shows_a_confirmation()
    {
        using var ctx = NewContext(out _, out var settingsStore, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("#telemetry-address").Input("https://example.test:7777");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        settingsStore.Load().TelemetryServerAddress.Should().Be("https://example.test:7777");
        cut.Markup.Should().Contain("Restart the app");
    }

    [Fact]
    public void Clicking_the_backdrop_invokes_OnClose()
    {
        using var ctx = NewContext(out _, out _, out _);
        var closed = false;

        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.OnClose, () => closed = true));
        cut.Find("div").Click();

        closed.Should().BeTrue();
    }

    [Fact]
    public void Clicking_the_close_x_invokes_OnClose()
    {
        using var ctx = NewContext(out _, out _, out _);
        var closed = false;

        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.OnClose, () => closed = true));
        cut.FindAll("button").First().Click();

        closed.Should().BeTrue();
    }

    [Fact]
    public void Starting_reset_shows_the_confirmation_prompt_requiring_RESET()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();

        cut.Markup.Should().Contain("RESET");
        var confirm = cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset"));
        confirm.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Typing_the_exact_confirmation_word_enables_the_confirm_button()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("type") == "text" && i.GetAttribute("id") != "telemetry-address").Input("RESET");

        var confirm = cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset"));
        confirm.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Confirming_a_reset_invokes_OnClose_and_clears_live_events_but_not_log_lines()
    {
        using var ctx = NewContext(out var liveDataStore, out _, out _);
        var closed = false;
        var changedRaised = false;
        var logLinesChangedRaised = false;
        liveDataStore.Changed += () => changedRaised = true;
        liveDataStore.LogLinesChanged += () => logLinesChangedRaised = true;

        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.OnClose, () => closed = true));
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("type") == "text" && i.GetAttribute("id") != "telemetry-address").Input("RESET");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset")).Click();

        closed.Should().BeTrue();
        changedRaised.Should().BeTrue("Reset Stats clears live events, which raises LiveDataStore.Changed");
        logLinesChangedRaised.Should().BeFalse("Reset Stats must not touch the Console tab's log buffer");
    }

    [Fact]
    public void Confirming_a_purge_clears_both_live_events_and_log_lines()
    {
        using var ctx = NewContext(out var liveDataStore, out _, out _);
        var changedRaised = false;
        var logLinesChangedRaised = false;
        liveDataStore.Changed += () => changedRaised = true;
        liveDataStore.LogLinesChanged += () => logLinesChangedRaised = true;

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear History")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("type") == "text" && i.GetAttribute("id") != "telemetry-address").Input("PURGE");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).Click();

        changedRaised.Should().BeTrue("Clear History clears live events, which raises LiveDataStore.Changed");
        logLinesChangedRaised.Should().BeTrue("Clear History also empties the Console tab's log buffer");
    }

    [Fact]
    public void Clicking_cancel_returns_to_the_initial_two_button_view()
    {
        using var ctx = NewContext(out _, out _, out _);

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
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear History")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("type") == "text" && i.GetAttribute("id") != "telemetry-address").Input("RESET");

        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).HasAttribute("disabled").Should().BeTrue();

        cut.FindAll("input").First(i => i.GetAttribute("type") == "text" && i.GetAttribute("id") != "telemetry-address").Input("PURGE");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Adaptive_routing_defaults_off_with_the_recommended_sample_size()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Off");
        cut.Find("#sample-size").GetAttribute("value").Should().Be("20000");
    }

    [Fact]
    public void Loads_a_persisted_adaptive_routing_toggle_and_sample_size()
    {
        var client = new FakeRouterSettingsAdminClient { Settings = new RouterSettingsInfo(true, 5_000) };
        using var ctx = NewContext(out _, out _, out _, client);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("On");
        cut.Find("#sample-size").GetAttribute("value").Should().Be("5000");
    }

    [Fact]
    public void Clicking_the_toggle_flips_it()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("button[aria-label='Toggle adaptive routing']").Click();

        cut.Markup.Should().Contain("On");
    }

    [Fact]
    public void Leaving_the_sample_size_field_clamps_it_into_bounds()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();
        var input = cut.Find("#sample-size");
        input.Change("999999");

        cut.Find("#sample-size").GetAttribute("value").Should().Be("50000");
    }

    [Fact]
    public void The_warning_icon_appears_below_the_recommended_sample_size_and_not_at_or_above_it()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.Markup.Should().NotContain("a sample size of 20000 is recommended.");

        cut.Find("#sample-size").Input("19999");
        cut.Markup.Should().Contain("a sample size of 20000 is recommended.");

        cut.Find("#sample-size").Input("20000");
        cut.Markup.Should().NotContain("a sample size of 20000 is recommended.");
    }

    [Fact]
    public void The_unified_save_button_persists_both_the_telemetry_address_and_the_router_settings()
    {
        using var ctx = NewContext(out _, out var settingsStore, out var routerSettingsStore);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("#telemetry-address").Input("https://example.test:7777");
        cut.Find("button[aria-label='Toggle adaptive routing']").Click();
        cut.Find("#sample-size").Input("15000");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        settingsStore.Load().TelemetryServerAddress.Should().Be("https://example.test:7777");
        cut.Markup.Should().Contain("Restart the app");
        routerSettingsStore.Settings.Should().Be(new RouterSettingsInfo(true, 15_000));
        cut.Markup.Should().Contain("Applied");
    }

    [Fact]
    public void Clearing_the_sample_size_field_to_type_a_new_value_does_not_snap_back()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("#sample-size").Input("");

        cut.Find("#sample-size").GetAttribute("value").Should().BeEmpty();
    }

    [Fact]
    public void Editing_a_field_after_a_save_clears_the_stale_outcome_message()
    {
        using var ctx = NewContext(out _, out _, out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();
        cut.Markup.Should().Contain("Applied");

        cut.Find("button[aria-label='Toggle adaptive routing']").Click();

        cut.Markup.Should().NotContain("Applied");
    }

    [Fact]
    public void A_save_while_the_router_is_unreachable_reports_it_without_blocking_the_telemetry_save()
    {
        var client = new FakeRouterSettingsAdminClient
        {
            Failure = new RouterSettingsAdminException("Could not save the router settings: the router is not reachable.", isUnavailable: true),
        };
        using var ctx = NewContext(out _, out var settingsStore, out _, client);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("#telemetry-address").Input("https://example.test:7777");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        settingsStore.Load().TelemetryServerAddress.Should().Be("https://example.test:7777");
        cut.Markup.Should().Contain("Could not reach the router. Is the proxy running?");
    }
}

