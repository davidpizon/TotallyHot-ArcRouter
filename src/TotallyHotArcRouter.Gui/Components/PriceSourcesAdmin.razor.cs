using Microsoft.JSInterop;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using PointerEventArgs = Microsoft.AspNetCore.Components.Web.PointerEventArgs;

namespace TotallyHot.ArcRouter.Gui.Components;

/// <summary>
/// Governance &gt; Price Sources pane: enable/disable each model price feed and pull fresh data on demand.
/// Talks to the proxy's PriceSourceAdminService gRPC API via the injected <see cref="PriceSourceStore"/>.
/// </summary>
/// <remarks>
/// Shows feed metadata only - row counts and schedule, never prices. That's D5 (licensing), not a layout
/// choice: see docs/router/model-price-catalog.md.
/// </remarks>
public partial class PriceSourcesAdmin
{
    // FLIP (First-Last-Invert-Play) reorder animation, wired through js/reorder-flip.js. The grab
    // handle is the sole reorder affordance (DESIGN.md §5.3), so the only place cards change rank is
    // the drag gesture below; this just makes the settle after a drop slide rather than teleport.
    private const string FlipContainerId = "price-source-stack";
    private const string FlipItemSelector = "[data-flip-key]";
    private const int FlipDurationMs = 200; // --dur-default (MOTION.md §3)

    private const string FlipEasing = "cubic-bezier(0.22, 1, 0.36, 1)"; // --ease-settle (MOTION.md §5)

    // The countdown renders whole minutes, so a minute is as often as it can change. Ticking per second to
    // re-render text that is identical 59 times out of 60 would be pure waste against a 4-12h interval.
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    // True only once the press has actually become a drag - JS reports this via DragStarted once the
    // pointer has travelled its threshold. A bare pointerdown must not set it: pointerdown on the
    // enable/disable toggle bubbles up to the card, and lifting on that would make every toggle click
    // flicker. This is what .card-lifted renders on.
    private bool _dragActive;

    // Drag state. While a drag (or its commit) is in flight the panel renders a private working order
    // (_order) instead of Store.Sources, so the cards reflow live to open a gap under the pointer; the
    // lifted card is identified by _dragName and drawn raised.
    private string? _dragName;

    private string? _opError;
    private List<PriceSourceStatus>? _order;

    private bool _pendingFlip;

    // Handed to js/reorder-flip.js so the drag can call back in. Created once and reused: a new
    // reference per drag would leak one per gesture.
    private DotNetObjectReference<PriceSourcesAdmin>? _selfRef;
    private Timer? _tick;

    /// <summary>
    /// The order to render: the live drag working-order while a drag or its commit is in flight, otherwise
    /// the store's own ranked order.
    /// </summary>
    private IReadOnlyList<PriceSourceStatus> DisplayOrder => _order ?? Store.Sources;

    /// <summary>
    /// Unsubscribes from <see cref="PriceSourceStore.Changed"/>, stops the countdown timer, and drops the
    /// reference the JS drag calls back through.
    /// </summary>
    /// <remarks>
    /// A drag can be live at this point (a tab switch tears the whole subtree down mid-gesture). This is
    /// synchronous and cannot await interop to tell JS to stop, so the JS side guards instead: its pointer
    /// handlers check <c>document.contains</c> and tear themselves down once the card leaves the DOM.
    /// </remarks>
    public void Dispose()
    {
        Store.Changed -= OnStoreChanged;
        _tick?.Dispose();
        _selfRef?.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task OnInitializedAsync()
    {
        Store.Changed += OnStoreChanged;

        // Drives only the countdown's own re-render. Deliberately not a poll: it re-reads the clock, never
        // the router. The panel refreshes its data when the user acts, and a background timer quietly
        // issuing gRPC calls forever is not something a Governance pane should do on its own.
        _tick = new Timer(callback: _ => InvokeAsync(StateHasChanged), null, dueTime: TickInterval,
            period: TickInterval);

        await Store.LoadAsync();
    }

    /// <summary>Re-renders when the store's state changes.</summary>
    private void OnStoreChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    /// <summary>Clears the inline error and reloads price sources. Implements the unreachable state's Retry button.</summary>
    private async Task Reload()
    {
        _opError = null;
        await Store.LoadAsync();
    }

    /// <summary>Flips one source's enabled state.</summary>
    private Task Toggle(PriceSourceStatus source)
    {
        return RunAsync(() => Store.SetEnabledAsync(name: source.Name, enabled: !source.Enabled));
    }

    /// <summary>Runs an ingestion cycle now. Implements the Pull Now button.</summary>
    private Task PullNow()
    {
        return RunAsync(() => Store.RefreshAsync());
    }

    /// <summary>
    /// Plays the FLIP settle animation queued by the JS drag, once the reordered list has rendered.
    /// </summary>
    /// <remarks>
    /// Runs both mid-drag and on drop. Mid-drag the JS captures the outgoing positions immediately before
    /// calling <see cref="MoveDraggedTo"/>, so this animates the other cards gliding into their new slots
    /// rather than jumping; the dragged card's own slot is skipped JS-side, since transforming it would
    /// re-trap the detached card inside the clip chain (DESIGN.md §5.5).
    /// </remarks>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_pendingFlip) return;

        _pendingFlip = false;
        await Js.InvokeVoidAsync(identifier: "reorderFlip.play", $"#{FlipContainerId}", FlipItemSelector,
            FlipDurationMs, FlipEasing);
    }

    /// <summary>
    /// Arms a drag and hands it to <c>js/reorder-flip.js</c>, which owns it from here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reordering is built on pointer events rather than HTML5 drag-and-drop because WinUI's WebView2 - the
    /// host BlazorWebView uses on Windows - never delivers in-page drag events (microsoft-ui-xaml#10576).
    /// A draggable card there is inert. Pointer events are ordinary DOM events and unaffected, which is why
    /// split-pane.js already drags with them.
    /// </para>
    /// <para>
    /// Only the press is handled in C#. Tracking the pointer from here would mean one interop round-trip per
    /// pointermove - 60-120 a second - which lags the card visibly behind the cursor, so JS owns the card's
    /// position and calls back only when the target index actually changes.
    /// </para>
    /// </remarks>
    private async Task OnPointerDown(PriceSourceStatus source, PointerEventArgs e)
    {
        // Button 0 is the primary button; a right-click is asking for a context menu, not a reorder.
        if (e.Button != 0 || Store.Sources.Count < 2 || Store.IsRefreshing) return;

        _dragName = source.Name;
        _dragActive = false;
        _selfRef ??= DotNetObjectReference.Create(this);

        await Js.InvokeVoidAsync(
            identifier: "reorderFlip.startDrag",
            $"#{FlipContainerId}",
            FlipItemSelector,
            source.Name,
            e.ClientY,
            _selfRef);
    }

    /// <summary>
    /// Called from JS once the press has travelled far enough to count as a drag, which is when the card
    /// is detached. Renders the lift; the card is already following the cursor by this point.
    /// </summary>
    /// <remarks>
    /// The working order is snapshotted here rather than in <see cref="OnPointerDown"/>, even though a
    /// press always fires <see cref="OnPointerDown"/> first. A plain click on the enable/disable toggle
    /// also fires <c>pointerdown</c> - it bubbles up from the button before the click completes - but
    /// never crosses the drag threshold, so JS's <c>_onUp</c> tears itself down without ever calling
    /// <see cref="EndDrag"/> or <see cref="CancelDrag"/>. Snapshotting eagerly in
    /// <see cref="OnPointerDown"/> left <c>_order</c> permanently set after every such click, which made
    /// <see cref="DisplayOrder"/> keep rendering that stale pre-toggle snapshot instead of the store's
    /// freshly toggled state until the component was torn down and recreated. Snapshotting only once a
    /// real drag is confirmed means a plain click never sets <c>_order</c> at all.
    /// </remarks>
    [JSInvokable]
    public async Task DragStarted()
    {
        if (_dragName is null) return;

        _order = Store.Sources.ToList();
        _dragActive = true;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Called from JS when the dragged card has moved closest to a different rank. Moves it there in the
    /// working order so the other cards reflow around it.
    /// </summary>
    /// <param name="index">The rank the card should now occupy, from the JS-side position math.</param>
    /// <remarks>
    /// JS captures the outgoing card positions immediately before calling this, so the re-render it causes
    /// is FLIP-animated by <see cref="OnAfterRenderAsync"/> and the other cards glide rather than jump.
    /// </remarks>
    [JSInvokable]
    public async Task MoveDraggedTo(int index)
    {
        if (_dragName is null || _order is null) return;

        var from = _order.FindIndex(s => s.Name == _dragName);
        var to = Math.Clamp(value: index, 0, max: _order.Count - 1);
        if (from < 0 || from == to) return;

        var dragged = _order[from];
        _order.RemoveAt(from);
        _order.Insert(index: to, item: dragged);
        _dragActive = true;
        _pendingFlip = true;

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Called from JS when the pointer is released after a real drag. Commits the reorder.
    /// </summary>
    /// <remarks>
    /// A drag can jump several ranks at once, so the whole working order is sent as one list rather than a
    /// sequence of adjacent swaps. The working order deliberately stays on screen across the round-trip so
    /// the cards do not flash back to their old ranks while the router confirms - which is also what keeps
    /// the slot still while JS animates the released card down into it.
    /// </remarks>
    [JSInvokable]
    public async Task EndDrag()
    {
        var order = _order;
        _dragName = null;

        if (order is null) return;

        var names = order.Select(s => s.Name).ToList();
        if (names.SequenceEqual(Store.Sources.Select(s => s.Name)))
        {
            // A drag that ended back where it started: nothing to persist.
            CancelDrag();
            await InvokeAsync(StateHasChanged);
            return;
        }

        try
        {
            await RunAsync(() => Store.ReorderAsync(names));
        }
        finally
        {
            // Hand rendering back to the store: on success its order now matches, on failure it reverts.
            _order = null;
            _dragActive = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Discards the in-progress drag's working order, reverting the list to the store's own ranked order.</summary>
    private void CancelDrag()
    {
        _dragName = null;
        _order = null;
        _dragActive = false;
    }

    /// <summary>
    /// Runs a mutation, surfacing a failure in the inline error banner rather than letting it escape into
    /// the renderer. Same wrapper shape as ProvidersAdmin's.
    /// </summary>
    private async Task<bool> RunAsync(Func<Task> operation)
    {
        _opError = null;
        try
        {
            await operation();
            return true;
        }
        catch (PriceSourceAdminException ex)
        {
            _opError = ex.Message;
            return false;
        }
    }

    /// <summary>The most recent pull's per-source outcome for the given source, or <see langword="null"/> if none has run.</summary>
    private PriceRefreshOutcome? LastOutcomeFor(string sourceName)
    {
        return Store.LastRefreshOutcomes.FirstOrDefault(o =>
            string.Equals(a: o.Source, b: sourceName, comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Renders the time until the next scheduled pull, coarsely - whole minutes, and hours once there is an
    /// hour to show.
    /// </summary>
    /// <remarks>
    /// It stops at "due now" rather than counting into negative time or auto-refreshing: the panel cannot see
    /// the cycle start, only its own arithmetic, so "due now" is the last thing it actually knows. The pull
    /// itself lands whether or not anyone is looking, and the next interaction - or Pull Now - brings back
    /// fresh counts and a fresh anchor.
    /// </remarks>
    private string DescribeCountdown()
    {
        if (Store.TimeUntilNextPull is not { } remaining) return string.Empty;

        // TimeUntilNextPull floors at zero, so this is "due or overdue" - the cycle may be running right now.
        if (remaining == TimeSpan.Zero) return "Next pull due now";

        // The interval is capped at 12h (D4), so hours is the largest unit this ever needs.
        return remaining switch
        {
            { TotalMinutes: < 1 } => "Next pull in under a minute",
            { TotalHours: < 1 } => $"Next pull in {(int)remaining.TotalMinutes}m",
            _ => $"Next pull in {(int)remaining.TotalHours}h {remaining.Minutes}m"
        };
    }

    /// <summary>
    /// The countdown's tooltip: the absolute local time it is counting to, plus the cadence. The countdown
    /// answers "how long?" at a glance; this answers "when, exactly?" without spending header space on it.
    /// </summary>
    private string DescribeSchedule()
    {
        return Store.Schedule is not { } schedule
            ? string.Empty
            : $"Scheduled for {schedule.NextPullUtc.ToLocalTime():t} local, every {schedule.PollInterval.TotalHours:0.#}h. " +
              "Pulling now resets this.";
    }
}