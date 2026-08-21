using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Runs one ingestion cycle: fetch every enabled source, upsert what it returns, and report the outcome.
/// It loops over the registry rather than special-casing the single source, so a second client needs no
/// change here.
/// </summary>
/// <remarks>
/// The zero-fresh-prices Error (D4) is logged here, once per completed cycle, so both the startup health
/// check and the background poll loop get it for free. With LiteLLM and OpenRouter both live, the "one
/// source fails, another succeeds" rung is now reachable: a cycle where exactly one enabled source throws no
/// longer means zero fresh prices by construction, so it logs that one source's per-source Warning (below)
/// and stops there, rather than always escalating to the cycle-level Error the way it did when there was
/// only ever one source to fail.
/// </remarks>
public sealed class PriceCatalogIngestionService
{
    // The 24h freshness floor (D1/D4). Not derived from PollIntervalHours: it is how often the underlying
    // prices can move, a different fact from how often we wake up to check.
    private static readonly TimeSpan FreshnessFloor = TimeSpan.FromHours(24);

    /// <summary>
    /// The <see cref="SourceOutcome.Error"/> recorded when a source is switched off mid-fetch. A distinct,
    /// stable string rather than an exception message: this is the operator's own action reported back, not
    /// a fault, and the panel and tests both key off it.
    /// </summary>
    public const string DisabledDuringFetch = "disabled during fetch";

    private readonly IPriceSourceRegistry _registry;
    private readonly PriceCatalogRepository _repository;
    private readonly PriceSourceToggleStore _toggleStore;
    private readonly ILogger<PriceCatalogIngestionService> _logger;
    private readonly IModelPriceCatalog? _priceCatalog;

    // Single-flight gate. The background poll loop and the Governance panel's "Pull Now" both land here, and
    // two concurrent cycles would double-fetch every source and race their upserts against each other. A
    // manual pull that collides with a scheduled tick waits for it rather than duplicating it.
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    // UTC ticks of the current interval's anchor. Read by the poll loop to decide when it is next due and by
    // the admin service to report the schedule, both off-thread from the cycle that writes it - hence
    // Interlocked rather than a plain field: a DateTimeOffset is too wide to assign atomically, and a torn
    // read here would be a garbage countdown or a mistimed poll.
    private long _scheduleAnchorTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceCatalogIngestionService"/> class.
    /// </summary>
    /// <param name="registry">The enabled price-source clients this service fetches from each cycle.</param>
    /// <param name="repository">The catalog repository each cycle's normalized prices are upserted into.</param>
    /// <param name="toggleStore">Owns each source's enabled state (D6), including the mid-fetch cancellation token.</param>
    /// <param name="logger">Structured log sink for per-source outcomes and D4's zero-fresh-prices error.</param>
    /// <param name="priceCatalog">
    /// Optional read-side cache to invalidate once a cycle has actually written rows - Phase 4's eviction
    /// signal. When <see langword="null"/> (tests constructing this type directly), nothing is invalidated
    /// and ingestion behaves exactly as before; the cache is a read-path concern, not an ingestion one.
    /// </param>
    public PriceCatalogIngestionService(
        IPriceSourceRegistry registry,
        PriceCatalogRepository repository,
        PriceSourceToggleStore toggleStore,
        ILogger<PriceCatalogIngestionService> logger,
        IModelPriceCatalog? priceCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(toggleStore);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _repository = repository;
        _toggleStore = toggleStore;
        _logger = logger;
        _priceCatalog = priceCatalog;

        // Construction time seeds the anchor so the schedule is well-defined before cycle #1. Without it a
        // router with every source disabled - which skips the startup cycle entirely
        // (StartupHealthCheckHostedService check 2) - would have no anchor at all, and the poll loop would
        // read "never ran" as "overdue" and fire the instant it started.
        _scheduleAnchorTicks = DateTimeOffset.UtcNow.UtcTicks;
    }

    /// <summary>
    /// Gets the instant the current poll interval started counting: the last cycle to complete, or this
    /// service's construction when none has. The next scheduled pull is this plus
    /// <see cref="PriceCatalogOptions.PollIntervalHours"/>.
    /// </summary>
    /// <remarks>
    /// Every live-pull cycle re-anchors this, whichever caller ran it - the poll loop, the startup check, or
    /// the Governance panel's Pull Now. That is what makes "any pull resets the clock" true of the poll loop
    /// and of the panel's countdown alike, from one fact rather than two that could drift.
    /// <para>
    /// It moves on <em>completion</em>, not success: a cycle in which every source failed still consumed the
    /// interval and still pushes the next poll out by a full one. This is why the countdown cannot be derived
    /// from any source's <c>last_updated_utc</c>, which only moves on a successful write.
    /// </para>
    /// <para>
    /// A reorder does <em>not</em> move this: <see cref="RecomputeWinnersAsync"/> re-resolves contested cells
    /// from prices already in storage without any network pull, so it hasn't consumed any of the poll
    /// interval and the countdown to the next real pull should carry on undisturbed.
    /// </para>
    /// </remarks>
    public DateTimeOffset ScheduleAnchorUtc =>
        new(Interlocked.Read(ref _scheduleAnchorTicks), TimeSpan.Zero);

    /// <summary>
    /// Fetches and upserts every enabled source, then evaluates the zero-fresh-prices condition. Serialized:
    /// a caller arriving while a cycle is running waits for it to finish before starting its own.
    /// </summary>
    public async Task<IngestionCycleSummary> RunCycleAsync(CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCycleCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Re-anchor on the way out, so the interval measures from when the cycle finished rather than
            // when it started - a slow cycle should not shorten the gap before the next one. In the finally
            // so a cycle that threw still counts: it consumed the interval either way, and an anchor that
            // only moved on success would leave a persistently failing feed being retried in a tight loop.
            Interlocked.Exchange(ref _scheduleAnchorTicks, DateTimeOffset.UtcNow.UtcTicks);
            _cycleGate.Release();
        }
    }

    /// <summary>
    /// Re-resolves every contested <c>(model, provider)</c> cell from prices already in storage, under the
    /// sources' current priority order - no network I/O, unlike <see cref="RunCycleAsync"/>. Backs the
    /// Governance panel's drag-to-reorder and the equivalent MCP tool: reordering must make the new priority
    /// actually take effect immediately, without waiting on - or forcing - a live pull. Shares
    /// <see cref="RunCycleAsync"/>'s single-flight gate, since both write <c>model_prices</c>, but does
    /// <em>not</em> re-anchor <see cref="ScheduleAnchorUtc"/>: it consumed none of the poll interval.
    /// </summary>
    public async Task<IngestionCycleSummary> RecomputeWinnersAsync(CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = _repository.RecomputeWinners();
            if (changed > 0)
            {
                _priceCatalog?.Invalidate();
            }

            var freshPriceCount = _repository.CountFreshPrices(FreshnessFloor);
            return new IngestionCycleSummary(freshPriceCount, Outcomes: []);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    /// <summary>
    /// Fetches and upserts prices from every enabled source client, tolerating per-source cancellation
    /// (a source disabled mid-fetch) and per-source exceptions without aborting the rest of the cycle, then
    /// summarizes the outcomes and logs an error if the cycle attempted sources but yielded zero fresh prices.
    /// </summary>
    private async Task<IngestionCycleSummary> RunCycleCoreAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<SourceOutcome>();
        var wroteAnyPrices = false;

        foreach (var client in _registry.EnabledClients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Linking the caller's token with the source's toggle token is what lets a disable abort a fetch
            // already in flight, rather than only taking effect from the next cycle (D6).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _toggleStore.GetSourceToken(client.Name));

            try
            {
                var prices = await client.FetchAsync(linked.Token).ConfigureAwait(false);

                // Re-check after the await: the source may have been disabled while this fetch was in
                // flight and lost the race against the cancellation above. Writing here would give a source
                // the operator just switched off a fresh last_updated_utc.
                if (!_toggleStore.IsEnabled(client.Name))
                {
                    outcomes.Add(new SourceOutcome(client.Name, Succeeded: false, PriceCount: 0, Error: DisabledDuringFetch));
                    _logger.LogInformation(
                        "Discarded {Count} prices from {Source}: it was disabled while the fetch was in flight.",
                        prices.Count,
                        client.Name);
                    continue;
                }

                // This priorityScore is a fallback for a source row that doesn't exist yet, used only by the
                // INSERT branch inside GetOrCreateSourceId. In normal operation it never applies: every known
                // source is seeded into aggregator_sources at startup (PriceCatalogDatabase.EnsureCreated),
                // so by the time ingestion runs, the row - and its operator-set rank - already exists, and the
                // priority gate below reads that stored rank, not this parameter.
                var written = _repository.UpsertPrices(client.Name, priorityScore: 0, prices, DateTimeOffset.UtcNow);
                wroteAnyPrices |= written > 0;

                outcomes.Add(new SourceOutcome(client.Name, Succeeded: true, PriceCount: written, Error: null));
                _logger.LogInformation(
                    "Price source {Source} refreshed {Count} prices.", client.Name, written);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Only the source's own token fired, so this is a user disabling the source mid-fetch: a
                // recorded outcome, and the loop moves on. The guard matters - without it the catch below
                // would let a toggle tear down the whole cycle, taking every other source's refresh with it.
                outcomes.Add(new SourceOutcome(client.Name, Succeeded: false, PriceCount: 0, Error: DisabledDuringFetch));
                _logger.LogInformation("Price source {Source} was disabled while its fetch was in flight.", client.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new SourceOutcome(client.Name, Succeeded: false, PriceCount: 0, Error: ex.Message));
                _logger.LogWarning(ex, "Price source {Source} failed to refresh.", client.Name);
            }

            // An OperationCanceledException with the caller's token cancelled falls through both catches and
            // propagates - the host is shutting down, and that is not a source failure to report.
        }

        // Phase 4's eviction signal: drop the read-side cache only when a source actually wrote rows, so a
        // cycle where every source failed - or returned nothing - leaves the cache serving the last prices
        // that were known good rather than forcing every reader back to SQLite for the same answers.
        if (wroteAnyPrices)
        {
            _priceCatalog?.Invalidate();
        }

        var freshPriceCount = _repository.CountFreshPrices(FreshnessFloor);
        var summary = new IngestionCycleSummary(freshPriceCount, outcomes);

        // Per D4: a cycle that ran at least one source but ended with zero fresh prices is an Error, not
        // silence - naming each source attempted and how it failed. Guarded on "had sources" so the
        // no-sources-enabled case is left to the startup check's Warning (D6), which owns that message.
        if (outcomes.Count > 0 && freshPriceCount == 0)
        {
            _logger.LogError(
                "Price ingestion cycle produced zero prices fresher than {FreshnessHours}h. Sources attempted: {Outcomes}.",
                FreshnessFloor.TotalHours,
                summary.DescribeOutcomes());
        }

        return summary;
    }
}

/// <summary>The result of one source's fetch within a cycle.</summary>
/// <param name="Source">The source name.</param>
/// <param name="Succeeded">Whether the fetch and upsert completed without error.</param>
/// <param name="PriceCount">How many price rows were written.</param>
/// <param name="Error">The failure message when <paramref name="Succeeded"/> is <see langword="false"/>.</param>
public sealed record SourceOutcome(string Source, bool Succeeded, int PriceCount, string? Error);

/// <summary>Summary of one ingestion cycle, consumed by the startup check (D4).</summary>
/// <param name="FreshPriceCount">Price rows fresher than the 24h floor after this cycle.</param>
/// <param name="Outcomes">Per-source results.</param>
public sealed record IngestionCycleSummary(int FreshPriceCount, IReadOnlyList<SourceOutcome> Outcomes)
{
    /// <summary>Renders the per-source outcomes as a compact, log-friendly string.</summary>
    public string DescribeOutcomes() =>
        Outcomes.Count == 0
            ? "(none)"
            : string.Join(
                ", ",
                Outcomes.Select(o => o.Succeeded
                    ? $"{o.Source}=ok({o.PriceCount})"
                    : $"{o.Source}=failed({o.Error})"));
}

