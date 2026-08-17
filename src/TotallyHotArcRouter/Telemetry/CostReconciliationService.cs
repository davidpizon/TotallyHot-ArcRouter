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
    private readonly Func<IReadOnlyList<IProviderCostReconciler>>? _reconcilerFactory;
    private readonly IUsageLedger _usageLedger;
    private readonly IProviderCostReconciliationStore _store;
    private readonly ILogger<CostReconciliationService> _logger;
    private readonly decimal _deltaWarningPercent;

    /// <summary>Initializes a new instance of the <see cref="CostReconciliationService"/> class.</summary>
    /// <param name="reconcilers">The fixed reconciler list used when <paramref name="reconcilerFactory"/> is <see langword="null"/>.</param>
    /// <param name="usageLedger">The local usage ledger each reconciled day's estimated cost is summed from.</param>
    /// <param name="store">Persists reconciliation snapshots and each provider's checkpoint cursor.</param>
    /// <param name="options">Provides <see cref="CostReconciliationOptions.DeltaWarningPercent"/>.</param>
    /// <param name="logger">Logs per-day failures and reported/local cost deltas.</param>
    /// <param name="reconcilerFactory">
    /// Optional. When supplied, called at the start of every <see cref="RunCycleAsync"/> instead of using
    /// the fixed <paramref name="reconcilers"/> snapshot - so a provider's Admin API key saved to the
    /// protected secret store after startup (docs/router/secrets-at-rest-plan.md §7) is picked up by the
    /// very next cycle rather than requiring a restart. Defaults to <see langword="null"/>, in which case
    /// behavior is unchanged from before this parameter existed.
    /// </param>
    public CostReconciliationService(
        IEnumerable<IProviderCostReconciler> reconcilers,
        IUsageLedger usageLedger,
        IProviderCostReconciliationStore store,
        IOptions<CostReconciliationOptions> options,
        ILogger<CostReconciliationService> logger,
        Func<IReadOnlyList<IProviderCostReconciler>>? reconcilerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(reconcilers);
        ArgumentNullException.ThrowIfNull(usageLedger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _reconcilers = reconcilers.ToList();
        _reconcilerFactory = reconcilerFactory;
        _usageLedger = usageLedger;
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
        var reconcilers = _reconcilerFactory?.Invoke() ?? _reconcilers;
        foreach (var reconciler in reconcilers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileProviderAsync(reconciler, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reconciles one provider from its stored checkpoint (or yesterday, on a first run) through yesterday,
    /// clamped to at most <see cref="MaxCatchUpDays"/> days so a long-idle provider catches up gradually
    /// rather than in one unbounded burst. Stops - without advancing the checkpoint further - at the first
    /// day whose reconciliation fails, so a gap is retried next cycle instead of being silently skipped.
    /// </summary>
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

    /// <summary>
    /// Fetches the provider-reported cost for <paramref name="day"/>, sums the local ledger's estimated
    /// cost over the same UTC day window, persists both as one reconciliation snapshot, and logs the delta.
    /// </summary>
    private async Task ReconcileDayAsync(IProviderCostReconciler reconciler, DateOnly day, CancellationToken cancellationToken)
    {
        var reportedCost = await reconciler.GetReportedCostAsync(day, cancellationToken).ConfigureAwait(false);

        var windowStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var windowEnd = windowStart.AddDays(1);

        // Sums usage_ledger directly over this exact UTC window, rather than IUsageRollupStore's
        // pre-aggregated buckets: a P1D rollup bucket's boundaries are computed in the pinned
        // Storage:RollupTimezone, not UTC, so querying it with a plain UTC-midnight window would silently
        // return nothing (localCost = 0) whenever that timezone isn't UTC - exactly the false "price table
        // is stale" signal the zero-localCost guard in LogDelta below exists to suppress, except here it'd
        // be a false positive from a mismatched query window, not a genuine no-traffic day.
        var localCost = _usageLedger.SumEstimatedCostUsd(reconciler.Provider, windowStart, windowEnd);

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

    /// <summary>
    /// Logs the reported-vs-local cost comparison for one provider/day - at Warning when the delta exceeds
    /// <see cref="CostReconciliationOptions.DeltaWarningPercent"/> (likely a stale price table), otherwise
    /// at Debug. Either cost being non-positive skips the percentage comparison entirely, since it would
    /// either divide by zero or always compute to a misleading 100%.
    /// </summary>
    private void LogDelta(string provider, DateOnly day, decimal reportedCost, decimal localCost)
    {
        if (reportedCost <= 0m || localCost <= 0m)
        {
            // reportedCost <= 0: no meaningful percentage difference against a zero base, never a
            // divide-by-zero warning. localCost <= 0: this proxy instance routed no traffic for the
            // provider that day while other org traffic did (ScopeNote above) - deltaPercent would always
            // compute to 100%, which would misread as "the local price table is stale/wrong" every time
            // this legitimate, expected gap occurs. Both cases get the same Debug-only treatment.
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
