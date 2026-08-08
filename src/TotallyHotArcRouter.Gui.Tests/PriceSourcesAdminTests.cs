using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Bunit;
using FluentAssertions;

// MAUI's implicit usings bring in a Microsoft.Maui.Controls.PointerEventArgs that collides with Blazor's.
using PointerEventArgs = Microsoft.AspNetCore.Components.Web.PointerEventArgs;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="PriceSourcesAdmin"/>: the Governance tab's price-source panel. Driven through a fake
/// <see cref="IPriceSourceAdminClient"/> so nothing here needs a live proxy or a gRPC channel.
/// </summary>
public sealed class PriceSourcesAdminTests
{
    private static Bunit.BunitContext NewContext(IPriceSourceAdminClient client)
    {
        var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton(new PriceSourceStore(client));
        return ctx;
    }

    [Fact]
    public void Renders_a_card_per_source_with_its_metadata()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 1247)));

        var cut = ctx.Render<PriceSourcesAdmin>();

        cut.Markup.Should().Contain("litellm");
        cut.Markup.Should().Contain("ENABLED");
        cut.Markup.Should().Contain("1,247");
        cut.Markup.Should().Contain("Pull Now");
    }

    [Fact]
    public void Renders_a_countdown_to_the_next_scheduled_pull()
    {
        // 6h interval anchored 2h ago leaves 4h, rendered coarse.
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10))
        {
            PollInterval = TimeSpan.FromHours(6),
            AnchorAge = TimeSpan.FromHours(2),
        });

        var cut = ctx.Render<PriceSourcesAdmin>();

        cut.Markup.Should().Contain("Next pull in 3h 59m");
    }

    [Fact]
    public void Renders_minutes_alone_inside_the_last_hour()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10))
        {
            PollInterval = TimeSpan.FromHours(6),
            AnchorAge = TimeSpan.FromMinutes(340),
        });

        var cut = ctx.Render<PriceSourcesAdmin>();

        cut.Markup.Should().Contain("Next pull in 19m");
        cut.Markup.Should().NotContain("0h");
    }

    [Fact]
    public void A_countdown_past_its_due_time_reads_due_now_rather_than_going_negative()
    {
        // The panel can see the schedule but not the cycle, so past the due time "due now" is the last thing
        // it actually knows. A negative number would read as a broken clock rather than a busy router.
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10))
        {
            PollInterval = TimeSpan.FromHours(6),
            AnchorAge = TimeSpan.FromHours(7),
        });

        var cut = ctx.Render<PriceSourcesAdmin>();

        cut.Markup.Should().Contain("Next pull due now");
        cut.Markup.Should().NotContain("Next pull in");
    }

    [Fact]
    public void Pulling_now_resets_the_countdown()
    {
        // The whole point of re-anchoring on any cycle: a manual pull buys a full interval, rather than
        // leaving the clock reading "in 4m" immediately after the user pulled.
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10))
        {
            PollInterval = TimeSpan.FromHours(6),
            AnchorAge = TimeSpan.FromMinutes(355),
        });
        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.Markup.Should().Contain("Next pull in 4m");

        cut.FindAll("button").First(b => b.TextContent.Contains("Pull Now", StringComparison.Ordinal)).Click();

        // Reset off the pull's own response - no follow-up call, no window showing a pull that already ran.
        cut.Markup.Should().Contain("Next pull in 5h 59m");
    }

    [Fact]
    public void The_countdown_is_absent_until_a_schedule_has_loaded()
    {
        // Unreachable router: there is no schedule to count to, and inventing one would be a guess rendered
        // as a fact.
        using var ctx = NewContext(new FakeClient
        {
            ListError = new PriceSourceAdminException("nope", isUnavailable: true),
        });

        var cut = ctx.Render<PriceSourcesAdmin>();

        cut.Markup.Should().NotContain("Next pull");
    }

    [Fact]
    public void Renders_a_disabled_source_as_not_served()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: false, PriorityScore: 0, PriceCount: 1247)));

        var cut = ctx.Render<PriceSourcesAdmin>();

        cut.Markup.Should().Contain("DISABLED");
        // The panel has to say the rows stop counting, not just that polling stopped - that's the half of D6
        // a user would otherwise assume works the other way.
        cut.Markup.Should().Contain("not polled, and its prices are not served");
    }

    [Fact]
    public void Clicking_the_toggle_flips_the_source()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.FindAll("button").First(b => b.TextContent.Contains("ENABLED", StringComparison.Ordinal)).Click();

        client.LastSetEnabled.Should().Be(("litellm", false));
        cut.Markup.Should().Contain("DISABLED");
    }

    [Fact]
    public void Clicking_pull_now_runs_a_cycle_and_reports_outcomes()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 0));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Pull Now", StringComparison.Ordinal)).Click();

        client.RefreshCount.Should().Be(1);
        cut.Markup.Should().Contain("Last pull refreshed 42 prices");
    }

    [Fact]
    public void Surfaces_a_failed_pull_outcome_inline()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 0))
        {
            RefreshOutcome = new PriceRefreshOutcome("litellm", Succeeded: false, PriceCount: 0, Error: "simulated source outage"),
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Pull Now", StringComparison.Ordinal)).Click();

        cut.Markup.Should().Contain("simulated source outage");
    }

    [Fact]
    public void Surfaces_a_rejected_toggle_in_the_error_banner()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10))
        {
            SetEnabledError = new PriceSourceAdminException("No price source named 'litellm' exists."),
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.FindAll("button").First(b => b.TextContent.Contains("ENABLED", StringComparison.Ordinal)).Click();

        // A rejected mutation must land in the panel, not escape into the renderer.
        cut.Markup.Should().Contain("No price source named");
    }

    [Fact]
    public void Renders_an_unreachable_state_when_the_router_is_down()
    {
        using var ctx = NewContext(new FakeClient
        {
            ListError = new PriceSourceAdminException("the router is not reachable.", isUnavailable: true),
        });

        var cut = ctx.Render<PriceSourcesAdmin>();

        // Degrades rather than throwing: the GUI outlives the proxy routinely.
        cut.Markup.Should().Contain("Router unreachable");
        cut.Markup.Should().Contain("Retry");
    }

    [Fact]
    public void A_toggle_that_finds_the_router_gone_falls_back_to_the_unreachable_state()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10))
        {
            SetEnabledError = new PriceSourceAdminException(
                "Could not disable 'litellm': the router is not reachable.",
                isUnavailable: true),
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.FindAll("button").First(b => b.TextContent.Contains("ENABLED", StringComparison.Ordinal)).Click();

        // The proxy died between the load and the click. The store's reachability has to follow, or the panel
        // keeps presenting a live-looking list of stale toggles.
        cut.Markup.Should().Contain("Router unreachable");
    }

    [Fact]
    public void A_rejected_toggle_keeps_the_panel_on_screen()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10))
        {
            SetEnabledError = new PriceSourceAdminException("Could not disable 'litellm': No price source named 'litellm' exists."),
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.FindAll("button").First(b => b.TextContent.Contains("ENABLED", StringComparison.Ordinal)).Click();

        // The router answered, so the data on screen is still good: the error belongs inline, and blanking the
        // panel into a "router down" state would be both wrong and would hide this message.
        cut.Markup.Should().NotContain("Router unreachable");
        cut.Markup.Should().Contain("No price source named");
        cut.Markup.Should().Contain("litellm");
    }

    [Fact]
    public void SingleSource_HasNoReorderControls()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10)));

        var cut = ctx.Render<PriceSourcesAdmin>();

        // Nothing to reorder against with one source - showing arrows that can never do anything is worse
        // than not showing them.
        cut.Markup.Should().NotContain("Rank higher");
        cut.Markup.Should().NotContain("Rank lower");
    }

    [Fact]
    public void TwoSources_TopRowsUpButtonIsRemoved_BottomRowsDownButtonIsRemoved()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();

        var upButtons = cut.FindAll("button").Where(b => b.GetAttribute("title") == "Rank higher").ToList();
        var downButtons = cut.FindAll("button").Where(b => b.GetAttribute("title") == "Rank lower").ToList();
        // litellm is first (higher rank): no Up button at all. openrouter is last: no Down button at all.
        Assert.Single(upButtons);
        Assert.Single(downButtons);
        Assert.False(upButtons[0].HasAttribute("disabled"));
        Assert.False(downButtons[0].HasAttribute("disabled"));
    }

    [Fact]
    public void Clicking_move_down_on_the_top_source_swaps_the_order()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.FindAll("button").First(b => b.GetAttribute("title") == "Rank lower").Click();

        client.ReorderCount.Should().Be(1);
        client.LastReorderRequest.Should().Equal("openrouter", "litellm");
        // The panel re-renders from the fake's own reordering, so openrouter should now read #1.
        cut.Markup.Should().Contain("#1");
    }

    [Fact]
    public void Clicking_move_up_on_the_bottom_source_swaps_the_order()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        // The bottom row's Up button, not the top row's (which is disabled) - both share the same title.
        cut.FindAll("button").Last(b => b.GetAttribute("title") == "Rank higher").Click();

        client.ReorderCount.Should().Be(1);
        client.LastReorderRequest.Should().Equal("openrouter", "litellm");
    }

    [Fact]
    public void Dragging_the_bottom_card_onto_the_top_card_moves_it_to_first()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        Card(cut, 1).PointerDown(new PointerEventArgs { Button = 0 });
        Card(cut, 0).PointerEnter(new PointerEventArgs { Buttons = 1 });
        cut.Find("div.space-y-3").PointerUp();

        client.ReorderCount.Should().Be(1);
        client.LastReorderRequest.Should().Equal("openrouter", "litellm");
    }

    [Fact]
    public void Releasing_on_the_card_the_drag_started_from_does_not_reorder()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        // A plain click - press and release without ever crossing onto another card. The toggle and the rank
        // arrows bubble their press up to the card, so this is what every click on them looks like from here.
        Card(cut, 0).PointerDown(new PointerEventArgs { Button = 0 });
        cut.Find("div.space-y-3").PointerUp();

        client.ReorderCount.Should().Be(0);
    }

    [Fact]
    public void A_press_that_was_released_outside_the_window_does_not_drag_on_re_entry()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        Card(cut, 1).PointerDown(new PointerEventArgs { Button = 0 });
        // Buttons: 0 - the pointer is back over a card with nothing held, so the release happened somewhere
        // we never saw it. The cards must not keep following the cursor.
        Card(cut, 0).PointerEnter(new PointerEventArgs { Buttons = 0 });
        cut.Find("div.space-y-3").PointerUp();

        client.ReorderCount.Should().Be(0);
    }

    /// <summary>
    /// The source cards, re-queried on each call: bUnit invalidates an element's event handlers when the
    /// render tree changes, so a held reference goes stale the moment a handler fires.
    /// </summary>
    private static AngleSharp.Dom.IElement Card(IRenderedComponent<PriceSourcesAdmin> cut, int index) =>
        cut.FindAll("div.space-y-3 > div.rounded-lg")[index];

    [Fact]
    public void No_razor_comment_leaks_into_the_rendered_markup()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5)));

        var cut = ctx.Render<PriceSourcesAdmin>();

        // A @* *@ comment written between an element's attributes is emitted as literal markup rather than
        // stripped, which reaches the browser as an invalid attribute name and takes the whole render down
        // with "An unhandled error has occurred". AngleSharp parses it happily, so nothing else here would
        // catch it - this panel's markup lost a day to exactly that.
        cut.Markup.Should().NotContain("@*");
    }

    [Fact]
    public void A_lone_source_card_does_not_offer_a_drag_cursor()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10)));

        var cut = ctx.Render<PriceSourcesAdmin>();

        // Same reasoning as the arrows: with nothing to reorder against, a card that invites a drag is a lie.
        Card(cut, 0).GetAttribute("class").Should().Contain("drag-disabled");
    }

    [Fact]
    public void A_rejected_reorder_is_surfaced_inline()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5))
        {
            ReorderError = new PriceSourceAdminException("The submitted order must name every existing price source exactly once."),
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.FindAll("button").First(b => b.GetAttribute("title") == "Rank lower").Click();

        cut.Markup.Should().Contain("must name every existing price source");
    }

    [Fact]
    public void A_pull_that_finds_the_router_gone_falls_back_to_the_unreachable_state()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10))
        {
            RefreshError = new PriceSourceAdminException(
                "Could not refresh the price sources: the router is not reachable.",
                isUnavailable: true),
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Pull Now", StringComparison.Ordinal)).Click();

        cut.Markup.Should().Contain("Router unreachable");
    }

    private sealed class FakeClient(params PriceSourceStatus[] sources) : IPriceSourceAdminClient
    {
        private List<PriceSourceStatus> _sources = [.. sources];

        // Mirrors the router's rule that any completed cycle re-anchors the schedule. A fake that returned a
        // fixed anchor would let a panel that ignores the refreshed schedule pass the reset test by accident.
        private DateTimeOffset _anchor = DateTimeOffset.UtcNow;

        /// <summary>The interval the fake router reports; the panel counts down to anchor + this.</summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromHours(6);

        /// <summary>Backdates the anchor so a test can land the countdown at a chosen point.</summary>
        public TimeSpan AnchorAge
        {
            init => _anchor = DateTimeOffset.UtcNow - value;
        }

        public PriceSourceAdminException? ListError { get; init; }

        public PriceSourceAdminException? SetEnabledError { get; init; }

        public PriceSourceAdminException? RefreshError { get; init; }

        public PriceSourceAdminException? ReorderError { get; init; }

        public PriceRefreshOutcome RefreshOutcome { get; init; } =
            new("litellm", Succeeded: true, PriceCount: 42, Error: null);

        public (string Name, bool Enabled)? LastSetEnabled { get; private set; }

        public IReadOnlyList<string>? LastReorderRequest { get; private set; }

        public int RefreshCount { get; private set; }

        public int ReorderCount { get; private set; }

        public Task<PriceSourceList> ListAsync(CancellationToken cancellationToken = default) =>
            ListError is not null
                ? Task.FromException<PriceSourceList>(ListError)
                : Task.FromResult(Snapshot());

        public Task<PriceSourceList> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
        {
            if (SetEnabledError is not null)
            {
                return Task.FromException<PriceSourceList>(SetEnabledError);
            }

            LastSetEnabled = (name, enabled);
            _sources = [.. _sources.Select(s => s.Name == name ? s with { Enabled = enabled } : s)];
            _anchor = DateTimeOffset.UtcNow;
            return Task.FromResult(Snapshot());
        }

        private PriceSourceList Snapshot() => new(_sources, new PriceSourceSchedule(PollInterval, _anchor));

        public Task<PriceRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (RefreshError is not null)
            {
                return Task.FromException<PriceRefreshResult>(RefreshError);
            }

            RefreshCount++;
            _sources = [.. _sources.Select(s => s.Name == RefreshOutcome.Source
                ? s with { PriceCount = RefreshOutcome.PriceCount }
                : s)];
            _anchor = DateTimeOffset.UtcNow;
            return Task.FromResult(new PriceRefreshResult(
                [RefreshOutcome],
                RefreshOutcome.PriceCount,
                _sources,
                new PriceSourceSchedule(PollInterval, _anchor)));
        }

        public Task<PriceRefreshResult> ReorderAsync(IReadOnlyList<string> namesInPriorityOrder, CancellationToken cancellationToken = default)
        {
            if (ReorderError is not null)
            {
                return Task.FromException<PriceRefreshResult>(ReorderError);
            }

            ReorderCount++;
            LastReorderRequest = namesInPriorityOrder;
            // Rank descending from submitted position, exactly like the real repository - the panel derives
            // rank purely from Sources' order, so a fake that didn't actually reorder would let a broken
            // MoveUp/MoveDown pass its test by accident.
            var byName = _sources.ToDictionary(s => s.Name);
            _sources = [.. namesInPriorityOrder.Select(name => byName[name])];
            _anchor = DateTimeOffset.UtcNow;
            return Task.FromResult(new PriceRefreshResult(
                [],
                _sources.Sum(s => s.PriceCount),
                _sources,
                new PriceSourceSchedule(PollInterval, _anchor)));
        }
    }
}

