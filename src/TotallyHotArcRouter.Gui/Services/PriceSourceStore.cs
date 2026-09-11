using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Price Sources panel. Wraps
/// <see cref="PriceSourceAdminClient"/> (the tested, platform-agnostic logic in TotallyHot.ArcRouter.Gui.Telemetry)
/// in the shared <see cref="AdminStoreBase{TClient}"/> shape, so the UI survives tab switches and
/// degrades gracefully when the proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class PriceSourceStore : AdminStoreBase<IPriceSourceAdminClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PriceSourceStore"/> class, creating and owning a client
    /// to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public PriceSourceStore(
        ILogger<PriceSourceStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(client: new PriceSourceAdminClient(serverAddress), logger: logger, ownsClient: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceSourceStore"/> class over a caller-supplied client.
    /// The seam tests use to drive the store without a live proxy; the caller owns the client's lifetime.
    /// </summary>
    /// <param name="client">The admin client to drive.</param>
    /// <param name="logger">Optional logger.</param>
    public PriceSourceStore(IPriceSourceAdminClient client, ILogger<PriceSourceStore>? logger = null)
        : base(client: client, logger: logger)
    {
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
            : Max(left: Schedule.NextPullUtc - DateTimeOffset.UtcNow, right: TimeSpan.Zero);

    /// <summary>The per-source results of the most recent manual pull, or empty if none has run.</summary>
    public IReadOnlyList<PriceRefreshOutcome> LastRefreshOutcomes { get; private set; } = [];

    /// <summary>Whether a manual pull is currently running, so the UI can disable the button.</summary>
    public bool IsRefreshing { get; private set; }

    /// <summary>Returns the larger of two <see cref="TimeSpan"/> values.</summary>
    private static TimeSpan Max(TimeSpan left, TimeSpan right)
    {
        return left > right ? left : right;
    }

    /// <summary>
    /// Loads the price source list. Connection failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>
    /// rather than thrown, so the tab renders an "unreachable" state instead of crashing when the proxy
    /// isn't running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct =>
            {
                var list = await Client.ListAsync(ct);
                Sources = list.Sources;
                Schedule = list.Schedule;
            },
            "load the price sources",
            cancellationToken);
    }

    /// <summary>
    /// Enables or disables a source, then publishes the updated list.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="LoadAsync"/>, this rethrows: a load failing means "we couldn't show you this", which
    /// the unreachable state covers, but a toggle failing means "the thing you just asked for did not
    /// happen", which the user has to be told inline. Same split as <see cref="ProviderAdminStore"/>.
    /// </remarks>
    /// <exception cref="GrpcAdminException">The toggle was rejected; the caller surfaces the message.</exception>
    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await Client.SetEnabledAsync(name: name, enabled: enabled,
                cancellationToken: cancellationToken);
            Sources = list.Sources;
            Schedule = list.Schedule;
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a price-source operation");
            throw;
        }

        RecordSuccess();
        NotifyChanged();
    }

    /// <summary>
    /// Runs an ingestion cycle now, waits for it, and publishes both the per-source outcomes and the updated
    /// list. <see cref="IsRefreshing"/> is true for the duration.
    /// </summary>
    /// <exception cref="GrpcAdminException">The pull could not be started or the router is unreachable.</exception>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return RunCycleAsync(() => Client.RefreshAsync(cancellationToken));
    }

    /// <summary>
    /// Rewrites every source's rank from <paramref name="namesInPriorityOrder"/>'s position, then recomputes
    /// the served price for every contested cell from prices already in storage under the new order - no
    /// network pull. The panel's drag-to-reorder control calls this rather than a separate "just save the
    /// order" method, because an order that hasn't been applied to the catalog yet is a promise, not a fact;
    /// the recompute, not a live pull, is what makes it one. Shares <see cref="IsRefreshing"/> with
    /// <see cref="RefreshAsync"/>: both are "please wait, an update is running" from the panel's point of view,
    /// even though only <see cref="RefreshAsync"/> reaches out to a source over the network.
    /// </summary>
    /// <exception cref="GrpcAdminException">
    /// The reorder was rejected (the name set didn't match every existing source), or the router is
    /// unreachable.
    /// </exception>
    public Task ReorderAsync(IReadOnlyList<string> namesInPriorityOrder, CancellationToken cancellationToken = default)
    {
        return RunCycleAsync(() =>
            Client.ReorderAsync(namesInPriorityOrder: namesInPriorityOrder, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Shared implementation behind <see cref="RefreshAsync"/> and <see cref="ReorderAsync"/>: runs the
    /// given ingestion-cycle operation, publishes its resulting sources, outcomes, and schedule, and
    /// tracks <see cref="IsRefreshing"/> for the duration.
    /// </summary>
    private async Task RunCycleAsync(Func<Task<PriceRefreshResult>> operation)
    {
        IsRefreshing = true;
        NotifyChanged();

        var refreshingCleared = false;

        try
        {
            var result = await operation();
            Sources = result.Sources;

            // A reorder's recompute carries no outcomes (no fetch happened), so this clears any outcome
            // banner left over from an earlier manual pull rather than letting it linger and read as if it
            // described the reorder that just ran.
            LastRefreshOutcomes = result.Outcomes;

            // The cycle that just ran re-anchored the schedule, so this is what resets the countdown after a
            // Pull Now - no follow-up call, and no window where the panel counts down to a pull that has
            // already happened.
            Schedule = result.Schedule;
            RecordSuccess();
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a price-source operation",
                beforeNotify: () =>
                {
                    IsRefreshing = false;
                    refreshingCleared = true;
                });
            throw;
        }
        finally
        {
            // The exception still propagates for the panel to render. RecordFailure's beforeNotify already
            // cleared IsRefreshing and published the failure's one notification, so this only runs (and
            // notifies) on the success path.
            if (!refreshingCleared)
            {
                IsRefreshing = false;
                NotifyChanged();
            }
        }
    }
}
