using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Writable, thread-safe source of truth for D6's per-source enable/disable toggle, backed by
/// <c>aggregator_sources.enabled</c>. Replaces the <c>PriceCatalog:Sources:&lt;name&gt;:Enabled</c>
/// configuration key that <see cref="PriceSourceRegistry"/> used to read once at construction, so a toggle
/// flipped from Governance → Price Sources takes effect live rather than at the next restart.
/// </summary>
/// <remarks>
/// Shaped after <see cref="TotallyHot.ArcRouter.Proxy.ProviderConfigStore"/>: an immutable snapshot swapped in one
/// atomic reference write, plus a <see cref="Changed"/> event. The database is authoritative; this holds a
/// cache so <see cref="IsEnabled"/> - which the ingestion loop calls per source per cycle - is a dictionary
/// read rather than a query. <see cref="List"/> deliberately does <em>not</em> use that cache; see its
/// remarks for why.
/// <para>
/// It also owns a <see cref="CancellationTokenSource"/> per source. Disabling a source cancels its token,
/// which is what makes "stop using this data the moment I switch it off" (D6) true even for a fetch already
/// in flight, rather than only from the next cycle onward.
/// </para>
/// </remarks>
public sealed class PriceSourceToggleStore : IDisposable
{
    private readonly PriceCatalogRepository _repository;
    private readonly ILogger<PriceSourceToggleStore> _logger;
    private readonly object _gate = new();

    // Always assigned a brand-new (never mutated) dictionary under _gate, and volatile so that swap is
    // published to IsEnabled's lock-free read - the same arrangement ModelRouteResolver._routes uses.
    // Volatile is doing real work here, not decoration: reference assignment is atomic, which only rules out
    // a torn read. It says nothing about visibility, so without this a reader taking no lock has no acquire
    // barrier and may keep observing a stale snapshot - which for this field means the ingestion loop
    // continuing to treat a source as enabled after the operator switched it off, defeating both the
    // per-cycle filter and the post-fetch re-check.
    private volatile IReadOnlyDictionary<string, PriceSourceState> _snapshot =
        new Dictionary<string, PriceSourceState>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, CancellationTokenSource> _sourceTokens =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceSourceToggleStore"/> class with an empty snapshot.
    /// </summary>
    /// <remarks>
    /// Deliberately does <em>not</em> read the database. This is a singleton, so the DI container constructs
    /// it while the host graph is built - before
    /// <see cref="TotallyHot.ArcRouter.Hosting.StartupHealthCheckHostedService"/> has run
    /// <see cref="PriceCatalogDatabase.EnsureCreated"/> - and querying a database whose tables do not exist
    /// yet would throw during construction. That check calls <see cref="Reload"/> immediately after creating
    /// the schema, which is what populates the snapshot. Until then every source reads as disabled, which is
    /// the safe direction: nothing polls before the startup check says the catalog is ready.
    /// </remarks>
    public PriceSourceToggleStore(PriceCatalogRepository repository, ILogger<PriceSourceToggleStore> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _logger = logger;
    }

    /// <summary>Raised after a toggle has been persisted and the snapshot swapped.</summary>
    public event Action? Changed;

    /// <summary>
    /// Re-reads every source's state from the database and swaps the snapshot. Called once at startup, and
    /// after each write so the cache reflects what was actually persisted rather than what was requested.
    /// </summary>
    public void Reload()
    {
        var states = _repository.GetSourceStates();
        var next = states.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            _snapshot = next;
        }
    }

    /// <summary>
    /// Gets every known source's current state, ordered by rank then name. Read live from the database, not
    /// from the cache.
    /// </summary>
    /// <remarks>
    /// Deliberately not served from <see cref="_snapshot"/>, and this is the whole reason the two reads are
    /// split. The snapshot exists for <see cref="IsEnabled"/>, which the ingestion loop calls per source per
    /// cycle; it is only refreshed when a toggle is written. But this method's payload also carries
    /// <see cref="PriceSourceState.PriceCount"/>, which every ingestion cycle changes without touching a
    /// toggle. Serving that from the cache meant the panel showed a stale count immediately after a manual
    /// pull - the user clicks "Pull Now", 2,500 prices are written, and the card still reads the old number,
    /// which looks exactly like the pull having failed. This is a small indexed group-by over a handful of
    /// rows, called by the panel and the startup check - never on the routing hot path - so reading through
    /// costs nothing worth caching.
    /// </remarks>
    public IReadOnlyList<PriceSourceState> List() =>
        [.. _repository.GetSourceStates()
            .OrderByDescending(s => s.PriorityScore)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Gets whether <paramref name="sourceName"/> should be polled and served.
    /// </summary>
    /// <remarks>
    /// An unknown source returns <see langword="false"/>. This inverts the old configuration default
    /// ("absent means enabled"), and deliberately: absence used to mean "the operator didn't mention it", but
    /// now every source with a client is seeded a row, so absence means the source has no client and cannot
    /// poll. Returning <see langword="true"/> for it would put a source into the ingestion loop that does not
    /// exist.
    /// </remarks>
    public bool IsEnabled(string sourceName) =>
        _snapshot.TryGetValue(sourceName, out var state) && state.Enabled;

    /// <summary>
    /// Persists a source's toggle, cancels its in-flight fetch when disabling, refreshes the cache, and
    /// raises <see cref="Changed"/>.
    /// </summary>
    /// <returns><see langword="false"/> when no source of that name exists; nothing is changed.</returns>
    public bool SetEnabled(string sourceName, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        if (!_repository.SetSourceEnabled(sourceName, enabled))
        {
            return false;
        }

        if (!enabled)
        {
            // Cancel before reloading: a fetch racing this call should see the cancellation, and the
            // re-check against the refreshed snapshot before upsert catches whatever slips past.
            CancelSource(sourceName);
        }

        Reload();

        _logger.LogInformation(
            "Price source {Source} was {State} from the Governance panel.",
            sourceName,
            enabled ? "enabled" : "disabled");

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Rewrites every source's rank from <paramref name="namesInPriorityOrder"/>'s position and raises
    /// <see cref="Changed"/>. Does not itself trigger a refresh - the caller (the gRPC service) does that,
    /// since re-resolving contested cells under the new order is a full ingestion cycle, not a cache
    /// invalidation this store owns.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the name set doesn't match every existing source exactly once; nothing is
    /// changed. See <see cref="PriceCatalogRepository.ReorderSources"/> for why that is rejected outright
    /// rather than best-effort applied.
    /// </returns>
    public bool Reorder(IReadOnlyList<string> namesInPriorityOrder)
    {
        ArgumentNullException.ThrowIfNull(namesInPriorityOrder);

        if (!_repository.ReorderSources(namesInPriorityOrder))
        {
            return false;
        }

        _logger.LogInformation(
            "Price sources reordered from the Governance panel: {Order}.",
            string.Join(" > ", namesInPriorityOrder));

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Gets a token that is cancelled when <paramref name="sourceName"/> is disabled. The ingestion loop
    /// links this with its own token so disabling a source aborts a fetch already in flight.
    /// </summary>
    public CancellationToken GetSourceToken(string sourceName)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GetOrCreateTokenSource(sourceName).Token;
        }
    }

    /// <summary>
    /// Removes and cancels the token source for <paramref name="sourceName"/>, if one exists, aborting any
    /// in-flight fetch that is linked to it.
    /// </summary>
    private void CancelSource(string sourceName)
    {
        CancellationTokenSource? toCancel;

        lock (_gate)
        {
            if (_disposed || !_sourceTokens.TryGetValue(sourceName, out toCancel))
            {
                return;
            }

            // Drop it from the map while holding the lock, so a concurrent GetSourceToken for a
            // *re-enabled* source builds a fresh one instead of handing out this already-cancelled token.
            // A cancelled token never resets, so reusing it would leave the source permanently unfetchable -
            // the source would read as enabled in the panel and never poll again, which is exactly the kind
            // of silent lie the toggle exists to avoid.
            _sourceTokens.Remove(sourceName);
        }

        // Cancel outside the lock: continuations registered on the token run inline on Cancel(), and one
        // could re-enter this store on the same thread. IsEnabled and List take no lock, but the methods that
        // do - GetSourceToken, SetEnabled's write path, Dispose - would then deadlock on the non-reentrant
        // _gate. Cancelling after releasing it keeps that reentrancy safe.
        toCancel.Cancel();
        toCancel.Dispose();
    }

    /// <summary>Returns the existing token source for <paramref name="sourceName"/>, creating one if none exists.</summary>
    private CancellationTokenSource GetOrCreateTokenSource(string sourceName)
    {
        if (!_sourceTokens.TryGetValue(sourceName, out var cts))
        {
            cts = new CancellationTokenSource();
            _sourceTokens[sourceName] = cts;
        }

        return cts;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<CancellationTokenSource> tokens;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            tokens = [.. _sourceTokens.Values];
            _sourceTokens.Clear();
        }

        foreach (var cts in tokens)
        {
            cts.Dispose();
        }
    }
}

