using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Runs one reconciliation cycle across every registered <see cref="IProviderCostReconciler"/> (§5.8),
/// mirroring <c>PriceCatalogIngestionService</c>'s testable-core-plus-hosted-poll-loop shape
/// (<see cref="Hosting.CostReconciliationHostedService"/> is the poll loop; this class is the cycle body,
/// callable directly by tests without a timer).
/// </summary>
public sealed class CostReconciliationService
{
    /// <summary>
    /// Caps how many past days a single cycle catches up on for one provider, so a proxy that was down for
    /// months doesn't fire an unbounded burst of provider API calls (each subject to its own rate limits)
    /// on its first cycle back. A provider more than this far behind stays behind by one day per cycle
    /// until it catches up - still bounded, just spread across more cycles.
    /// </summary>
    public const int MaxCatchUpDays = 7;

    private readonly IReadOnlyList<IProviderCostReconciler> _reconcilers;
    private readonly IUsageRollupStore _rollupStore;
    private readonly IProviderCostReconciliationStore _store;
    private readonly ILogger<CostReconciliationService> _logger;
    private readonly decimal _deltaWarningPercent;

    /// <summary>Initializes a new instance of the <see cref="CostReconciliationService"/> class.</summary>
    public CostReconciliationService(
        IEnumerable<IProviderCostReconciler> reconcilers,
        IUsageRollupStore rollupStore,
        IProviderCostReconciliationStore store,
        IOptions<CostReconciliationOptions> options,
        ILogger<CostReconciliationService> logger)
    {
        ArgumentNullException.ThrowIfNull(reconcilers);
        ArgumentNullException.ThrowIfNull(rollupStore);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _reconcilers = reconcilers.ToList();
        _rollupStore = rollupStore;
        _store = store;
        _logger = logger;
        _deltaWarningPercent = options.Value.DeltaWarningPercent;
    }

    /// <summary>
    /// Reconciles every registered provider in turn. Per provider, catches up from its checkpoint (or
    /// yesterday, on a first run) through yesterday - the most recent fully-closed UTC day (never the
    /// in-progress one, per §5.8's honeycomb discipline) - stopping at the first day whose fetch fails so
    /// the checkpoint never skips ahead of a gap.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        foreach (var reconciler in _reconcilers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileProviderAsync(reconciler, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileProviderAsync(IProviderCostReconciler reconciler, CancellationToken cancellationToken)
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var checkpoint = _store.GetLastReconciledDay(reconciler.Provider);

        // First run for this provider: reconcile only yesterday, not the provider's entire history - the
        // ledger only has data from whenever it started recording anyway, and a fresh install shouldn't
        // immediately fire MaxCatchUpDays worth of billing-API calls for days it has no local data for.
        var startDay = checkpoint?.AddDays(1) ?? yesterday;
        if (startDay > yesterday)
        {
            // Already caught up (a checkpoint from earlier today, before "yesterday" rolled forward).
            return;
        }

        var earliestAllowed = yesterday.AddDays(-(MaxCatchUpDays - 1));
        if (startDay < earliestAllowed)
        {
            startDay = earliestAllowed;
        }

        for (var day = startDay; day <= yesterday; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ReconcileDayAsync(reconciler, day, cancellationToken).ConfigureAwait(false);
                _store.SetLastReconciledDay(reconciler.Provider, day);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stop catching up further days until this one succeeds - advancing the checkpoint past a
                // failed day would silently skip it forever, since the next cycle starts from the checkpoint.
                _logger.LogWarning(
                    ex,
                    "Cost reconciliation failed for provider {Provider} on {Day}; will retry next cycle.",
                    reconciler.Provider,
                    day);
                return;
            }
        }
    }

    private async Task ReconcileDayAsync(IProviderCostReconciler reconciler, DateOnly day, CancellationToken cancellationToken)
    {
        var reportedCost = await reconciler.GetReportedCostAsync(day, cancellationToken).ConfigureAwait(false);

        var windowStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var windowEnd = windowStart.AddDays(1);

        // Reuses the exact same query Phase 4's rollup GUI/export surface reads - never a second,
        // independently-written local-cost query that could silently drift from the one everything else
        // already trusts.
        var localCost = _rollupStore
            .Query(windowStart, windowEnd, "P1D", "provider")
            .FirstOrDefault(bucket => string.Equals(bucket.GroupKey, reconciler.Provider, StringComparison.OrdinalIgnoreCase))
            ?.CostUsd ?? 0m;

        _store.InsertReconciliation(new ProviderCostReconciliationEntry(
            Provider: reconciler.Provider,
            WindowStartUtc: windowStart,
            WindowEndUtc: windowEnd,
            ProviderReportedCostUsd: reportedCost,
            LocalEstimatedCostUsd: localCost,
            ScopeNote: "Provider-reported cost is organization-wide (every key under the configured Admin " +
                "API key); the local estimate reflects only requests routed through this proxy instance - " +
                "a gap can be legitimate, not necessarily a pricing error.",
            FetchedAtUtc: DateTimeOffset.UtcNow));

        LogDelta(reconciler.Provider, day, reportedCost, localCost);
    }

    private void LogDelta(string provider, DateOnly day, decimal reportedCost, decimal localCost)
    {
        if (reportedCost <= 0m)
        {
            // No meaningful percentage difference against a zero base - log the raw comparison at Debug
            // only, never a divide-by-zero warning.
            _logger.LogDebug(
                "Cost reconciliation for provider {Provider} on {Day}: provider reported ${ReportedCost}, local estimate ${LocalCost}.",
                provider, day, reportedCost, localCost);
            return;
        }

        var deltaPercent = Math.Abs(reportedCost - localCost) / reportedCost * 100m;
        if (deltaPercent >= _deltaWarningPercent)
        {
            _logger.LogWarning(
                "Cost reconciliation for provider {Provider} on {Day}: local estimate ${LocalCost} vs " +
                "provider-reported ${ReportedCost} ({DeltaPercent:F1}% difference) - the local price table " +
                "may be stale or wrong for this provider's models.",
                provider, day, localCost, reportedCost, deltaPercent);
        }
        else
        {
            _logger.LogDebug(
                "Cost reconciliation for provider {Provider} on {Day}: local estimate ${LocalCost} vs " +
                "provider-reported ${ReportedCost} ({DeltaPercent:F1}% difference).",
                provider, day, localCost, reportedCost, deltaPercent);
        }
    }
}
