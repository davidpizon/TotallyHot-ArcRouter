using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using Bunit;
using AwesomeAssertions;

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
        // reorderFlip.capture/play are a cosmetic drag-settle animation - Loose so a drag test doesn't
        // have to stub out every rect/transition call to reach the reorder it's actually asserting on.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
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
    public void SingleSource_HasNoGrabHandle()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10)));

        var cut = ctx.Render<PriceSourcesAdmin>();

        // Nothing to reorder against with one source - showing a handle that can never do anything is worse
        // than not showing it.
        cut.Markup.Should().NotContain("Drag to reorder");
    }

    [Fact]
    public void TwoSources_EachCardHasAGrabHandle()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();

        // The grab handle is the only reorder affordance (DESIGN.md §5.3) - no per-row buttons at all.
        cut.FindAll("[title='Drag to reorder. Priority: higher wins a contested price.']").Should().HaveCount(2);
        cut.FindAll("button").Should().NotContain(b =>
            b.GetAttribute("title") == "Rank higher" || b.GetAttribute("title") == "Rank lower");
    }

    [Fact]
    public async Task Dragging_the_top_card_to_the_last_rank_moves_it_there()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        await DragAsync(cut, from: 0, toIndex: 1);

        client.ReorderCount.Should().Be(1);
        client.LastReorderRequest.Should().Equal("openrouter", "litellm");
        // The panel re-renders from the fake's own reordering, so openrouter should now read #1.
        cut.Markup.Should().Contain("#1");
    }

    [Fact]
    public async Task Dragging_the_bottom_card_to_the_first_rank_moves_it_there()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        await DragAsync(cut, from: 1, toIndex: 0);

        client.ReorderCount.Should().Be(1);
        client.LastReorderRequest.Should().Equal("openrouter", "litellm");
    }

    [Fact]
    public async Task A_drag_that_ends_on_the_rank_it_started_from_does_not_reorder()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        // Out to the other rank and back again before releasing. The working order ends up identical to the
        // store's, so there is nothing to persist - and, critically, the card must not be left detached over
        // the one it swapped with. That was the reported bug this drag model replaced.
        Card(cut, 0).PointerDown(new PointerEventArgs { Button = 0 });
        await cut.InvokeAsync(() => cut.Instance.DragStarted());
        await cut.InvokeAsync(() => cut.Instance.MoveDraggedTo(1));
        await cut.InvokeAsync(() => cut.Instance.MoveDraggedTo(0));
        await cut.InvokeAsync(() => cut.Instance.EndDrag());

        client.ReorderCount.Should().Be(0);
        cut.Markup.Should().NotContain("card-lifted");
        // Both cards still rendered - neither was left covered by a stale detached card.
        cut.Markup.Should().Contain("litellm");
        cut.Markup.Should().Contain("openrouter");
    }

    [Fact]
    public async Task A_press_that_never_becomes_a_drag_does_not_reorder()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5));
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        // Pointerdown, then a release that JS never promoted to a drag because the pointer never travelled
        // far enough. This is what every click on the enable/disable toggle looks like from the card.
        Card(cut, 0).PointerDown(new PointerEventArgs { Button = 0 });
        await cut.InvokeAsync(() => cut.Instance.EndDrag());

        client.ReorderCount.Should().Be(0);
        cut.Markup.Should().NotContain("card-lifted");
    }

    [Fact]
    public void Each_source_card_sits_in_its_own_slot()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5)));

        var cut = ctx.Render<PriceSourcesAdmin>();

        // The slot is the in-flow layout unit that holds the row open while its card is pinned out of
        // the flow, so there must be exactly one per source and the card must be its only child.
        var slots = cut.FindAll("div.ds-card-stack > div.ds-card-slot");
        slots.Should().HaveCount(2);
        slots.Should().OnlyContain(slot => slot.Children.Length == 1);
    }

    [Fact]
    public void The_flip_key_is_on_the_slot_not_the_card()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5)));

        var cut = ctx.Render<PriceSourcesAdmin>();

        // reorderFlip addresses items by this attribute. It has to land on the slot, which is always in
        // flow and never transformed, so the settle measures true layout even while a card is pinned. A
        // stray second copy on the card would make the FLIP pass double-match, which nothing else here
        // would catch.
        var keyed = cut.FindAll("[data-flip-key]");
        keyed.Should().HaveCount(2);
        keyed.Should().OnlyContain(el => el.ClassList.Contains("ds-card-slot"));
    }

    [Fact]
    public async Task A_lifted_card_is_rendered_pinned_so_re_renders_cannot_strip_it()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5)));

        var cut = ctx.Render<PriceSourcesAdmin>();
        Card(cut, 0).PointerDown(new PointerEventArgs { Button = 0 });
        await cut.InvokeAsync(() => cut.Instance.DragStarted());

        // JS adds card-pinned itself for immediacy, but Blazor has to render it too: Blazor rewrites the
        // whole class attribute on every render, starting with the one DragStarted triggers here. A card
        // that loses this class while JS is still writing viewport top/left into its inline style does not
        // just lose its styling - it reverts to position: relative and is flung out of the list.
        cut.Markup.Should().Contain("card-lifted");
        cut.Markup.Should().Contain("card-pinned");

        // And it must survive an unrelated re-render - the countdown ticks once a minute, and a store
        // refresh can land at any time, either of which rewrites that attribute mid-drag.
        await cut.InvokeAsync(() => cut.Instance.MoveDraggedTo(1));
        cut.Markup.Should().Contain("card-pinned");
    }

    [Fact]
    public void A_plain_click_never_lifts_a_card()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5)));

        var cut = ctx.Render<PriceSourcesAdmin>();
        // Pointerdown alone - what every click on the toggle looks like from the card, since the press
        // bubbles up before the click completes. The lift only arrives via DragStarted, which JS withholds
        // until the pointer has actually travelled, so nothing is lifted (or detached) here.
        Card(cut, 0).PointerDown(new PointerEventArgs { Button = 0 });

        cut.Markup.Should().NotContain("card-lifted");
    }

    [Fact]
    public void Pressing_a_card_hands_the_drag_to_js()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5)));

        var cut = ctx.Render<PriceSourcesAdmin>();
        Card(cut, 0).PointerDown(new PointerEventArgs { Button = 0, ClientY = 42 });

        // JS owns the drag from the press onward, so this handoff is the whole C# half of starting one.
        var start = ctx.JSInterop.Invocations.Should().ContainSingle(i => i.Identifier == "reorderFlip.startDrag").Subject;
        start.Arguments[0].Should().Be("#price-source-stack");
        start.Arguments[2].Should().Be("litellm");
        start.Arguments[3].Should().Be(42d);
    }

    [Fact]
    public void A_right_click_does_not_start_a_drag()
    {
        using var ctx = NewContext(new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5)));

        var cut = ctx.Render<PriceSourcesAdmin>();
        // Button 2 is the secondary button - that press is asking for a context menu, not a reorder.
        Card(cut, 0).PointerDown(new PointerEventArgs { Button = 2 });

        ctx.JSInterop.Invocations.Should().NotContain(i => i.Identifier == "reorderFlip.startDrag");
    }

    /// <summary>
    /// The source cards, re-queried on each call: bUnit invalidates an element's event handlers when the
    /// render tree changes, so a held reference goes stale the moment a handler fires. Each card sits
    /// inside its own `.ds-card-slot` wrapper - the slot is the in-flow layout unit, the card is what
    /// gets lifted out of the flow while dragging.
    /// </summary>
    private static AngleSharp.Dom.IElement Card(IRenderedComponent<PriceSourcesAdmin> cut, int index) =>
        cut.FindAll("div.ds-card-stack > div.ds-card-slot > div.rounded-lg")[index];

    /// <summary>
    /// Drives a whole drag the way the browser would: press the card, then replay the three callbacks
    /// <c>js/reorder-flip.js</c> makes back into the component. The pointer tracking and the index math
    /// live in JS and have no layout engine to run against here (bUnit renders no geometry), so the
    /// component is exercised through the same surface JS uses rather than through synthetic mouse events.
    /// </summary>
    private static async Task DragAsync(IRenderedComponent<PriceSourcesAdmin> cut, int from, int toIndex)
    {
        Card(cut, from).PointerDown(new PointerEventArgs { Button = 0 });
        await cut.InvokeAsync(() => cut.Instance.DragStarted());
        await cut.InvokeAsync(() => cut.Instance.MoveDraggedTo(toIndex));
        await cut.InvokeAsync(() => cut.Instance.EndDrag());
    }

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
    public async Task A_rejected_reorder_is_surfaced_inline()
    {
        var client = new FakeClient(
            new PriceSourceStatus("litellm", Enabled: true, PriorityScore: 0, PriceCount: 10),
            new PriceSourceStatus("openrouter", Enabled: true, PriorityScore: -10, PriceCount: 5))
        {
            ReorderError = new PriceSourceAdminException("The submitted order must name every existing price source exactly once."),
        };
        using var ctx = NewContext(client);

        var cut = ctx.Render<PriceSourcesAdmin>();
        await DragAsync(cut, from: 0, toIndex: 1);

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

