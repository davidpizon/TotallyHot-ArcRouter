using TotallyHot.ArcRouter.Gui.Telemetry;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Price Sources panel. Wraps
/// <see cref="PriceSourceAdminClient"/> (the tested, platform-agnostic logic in TotallyHot.ArcRouter.Gui.Telemetry)
/// with the same "singleton + Changed event + best-effort, reachability-tolerant" shape as
/// <see cref="ProviderAdminStore"/> and <see cref="LiveDataStore"/>, so the UI survives tab switches and
/// degrades gracefully when the proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class PriceSourceStore : IDisposable
{
    private readonly IPriceSourceAdminClient _client;
    private readonly IDisposable? _ownedClient;
    private readonly ILogger<PriceSourceStore>? _logger;

    /// <summary>Initializes a new instance of the <see cref="PriceSourceStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">The proxy's TLS gRPC endpoint; defaults to <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.</param>
    public PriceSourceStore(
        ILogger<PriceSourceStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        _logger = logger;
        var client = new PriceSourceAdminClient(serverAddress);
        _client = client;
        _ownedClient = client;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceSourceStore"/> class over a caller-supplied client.
    /// The seam tests use to drive the store without a live proxy; the caller owns the client's lifetime.
    /// </summary>
    public PriceSourceStore(IPriceSourceAdminClient client, ILogger<PriceSourceStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedClient = null;
        _logger = logger;
    }

    /// <summary>The price sources currently known, refreshed after each load or successful mutation.</summary>
    public IReadOnlyList<PriceSourceStatus> Sources { get; private set; } = [];

    /// <summary>
    /// When the router next pulls of its own accord, or <see langword="null"/> before the first successful
    /// load. Refreshed by every call that reaches the router, so a manual pull - which re-anchors the
    /// schedule - updates this from the same response that reports its outcomes.
    /// </summary>
    public PriceSourceSchedule? Schedule { get; private set; }

    /// <summary>
    /// Gets how long until the next scheduled pull, floored at zero, or <see langword="null"/> when no
    /// schedule has been loaded yet.
    /// </summary>
    /// <remarks>
    /// Floored rather than allowed to go negative: past the due time the honest answer is "any moment now",
    /// and the panel has no way to know whether the cycle is running, queued behind a slow one, or late.
    /// A negative countdown would read as a bug in the clock rather than a router being busy.
    /// </remarks>
    public TimeSpan? TimeUntilNextPull =>
        Schedule is null
            ? null
            : Max(Schedule.NextPullUtc - DateTimeOffset.UtcNow, TimeSpan.Zero);

    /// <summary>Returns the larger of two <see cref="TimeSpan"/> values.</summary>
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    /// <summary>The per-source results of the most recent manual pull, or empty if none has run.</summary>
    public IReadOnlyList<PriceRefreshOutcome> LastRefreshOutcomes { get; private set; } = [];

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load or mutation reached the proxy.</summary>
    /// <remarks>
    /// Only connectivity tracks this. An operation the router <em>rejected</em> (an unknown source, say) has
    /// still reached it, so it leaves this <see langword="true"/> and surfaces inline instead - otherwise one
    /// bad argument would blank a panel whose data is perfectly good.
    /// </remarks>
    public bool IsReachable { get; private set; }

    /// <summary>The message from the last failure to reach the proxy, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether a manual pull is currently running, so the UI can disable the button.</summary>
    public bool IsRefreshing { get; private set; }

    /// <summary>Raised after any of the above change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the price source list. Connection failures are swallowed and surfaced via
    /// <see cref="IsReachable"/>/<see cref="LastError"/> rather than thrown, so the tab renders an
    /// "unreachable" state instead of crashing when the proxy isn't running.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await _client.ListAsync(cancellationToken);
            Sources = list.Sources;
            Schedule = list.Schedule;
            IsReachable = true;
            LastError = null;
        }
        catch (PriceSourceAdminException ex)
        {
            IsReachable = false;
            LastError = ex.Message;
            _logger?.LogWarning(ex, "Failed to load price sources from the router.");
        }
        finally
        {
            IsLoaded = true;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Enables or disables a source, then publishes the updated list.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="LoadAsync"/>, this rethrows: a load failing means "we couldn't show you this", which
    /// the unreachable state covers, but a toggle failing means "the thing you just asked for did not
    /// happen", which the user has to be told inline. Same split as <see cref="ProviderAdminStore"/>.
    /// </remarks>
    /// <exception cref="PriceSourceAdminException">The toggle was rejected; the caller surfaces the message.</exception>
    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await _client.SetEnabledAsync(name, enabled, cancellationToken);
            Sources = list.Sources;
            Schedule = list.Schedule;
        }
        catch (PriceSourceAdminException ex)
        {
            RecordFailure(ex);
            throw;
        }

        IsReachable = true;
        IsLoaded = true;
        LastError = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Runs an ingestion cycle now, waits for it, and publishes both the per-source outcomes and the updated
    /// list. <see cref="IsRefreshing"/> is true for the duration.
    /// </summary>
    /// <exception cref="PriceSourceAdminException">The pull could not be started or the router is unreachable.</exception>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RunCycleAsync(() => _client.RefreshAsync(cancellationToken));

    /// <summary>
    /// Rewrites every source's rank from <paramref name="namesInPriorityOrder"/>'s position, then runs an
    /// ingestion cycle so contested cells re-resolve under the new order immediately - the panel's up/down
    /// controls call this rather than a separate "just save the order" method, because an order that hasn't
    /// been applied to the catalog yet is a promise, not a fact. Shares <see cref="IsRefreshing"/> with
    /// <see cref="RefreshAsync"/>: both are "please wait, a cycle is running" from the panel's point of view.
    /// </summary>
    /// <exception cref="PriceSourceAdminException">
    /// The reorder was rejected (the name set didn't match every existing source), or the router is
    /// unreachable.
    /// </exception>
    public Task ReorderAsync(IReadOnlyList<string> namesInPriorityOrder, CancellationToken cancellationToken = default) =>
        RunCycleAsync(() => _client.ReorderAsync(namesInPriorityOrder, cancellationToken));

    /// <summary>
    /// Shared implementation behind <see cref="RefreshAsync"/> and <see cref="ReorderAsync"/>: runs the
    /// given ingestion-cycle operation, publishes its resulting sources, outcomes, and schedule, and
    /// tracks <see cref="IsRefreshing"/> for the duration.
    /// </summary>
    private async Task RunCycleAsync(Func<Task<PriceRefreshResult>> operation)
    {
        IsRefreshing = true;
        Changed?.Invoke();

        try
        {
            var result = await operation();
            Sources = result.Sources;
            LastRefreshOutcomes = result.Outcomes;

            // The cycle that just ran re-anchored the schedule, so this is what resets the countdown after a
            // Pull Now - no follow-up call, and no window where the panel counts down to a pull that has
            // already happened.
            Schedule = result.Schedule;
            IsReachable = true;
            IsLoaded = true;
            LastError = null;
        }
        catch (PriceSourceAdminException ex)
        {
            RecordFailure(ex);
            throw;
        }
        finally
        {
            // In a finally so a failed cycle re-enables the buttons rather than leaving them stuck disabled.
            // The exception still propagates for the panel to render.
            IsRefreshing = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Reflects a failed mutation in the store's state before the caller rethrows, so
    /// <see cref="IsReachable"/> keeps its documented meaning after a mutation and not only after a load.
    /// </summary>
    /// <remarks>
    /// Only a connectivity failure moves <see cref="IsReachable"/>. A rejection reached the router and is the
    /// panel's inline error to render; treating it as unreachable would replace the whole panel with a
    /// "router down" state that is both wrong and hides the actual message.
    /// </remarks>
    private void RecordFailure(PriceSourceAdminException ex)
    {
        if (!ex.IsUnavailable)
        {
            return;
        }

        IsReachable = false;
        LastError = ex.Message;
        _logger?.LogWarning(ex, "The router became unreachable during a price-source operation.");
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose() => _ownedClient?.Dispose();
}

