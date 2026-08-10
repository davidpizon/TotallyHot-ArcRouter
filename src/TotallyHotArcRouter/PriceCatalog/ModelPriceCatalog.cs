using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// <see cref="IModelPriceCatalog"/> backed by <see cref="PriceCatalogRepository"/> with a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> in front of it. The cache is a <b>correctness enabler,
/// not a performance tweak</b>: the routing read happens inline with a live request, so it has to be an
/// in-memory hit - no read path may await I/O, and refreshing rows is always the background ingestion
/// service's job (see <c>docs/router/model-price-catalog.md</c> Phase 4).
/// </summary>
/// <remarks>
/// Misses are cached as well as hits. A model the catalog has never heard of is the common case on a
/// request path that prices every candidate, and re-querying SQLite for a row that is known not to exist
/// would defeat the point of caching at all. Both are stored as a nullable
/// <see cref="CatalogPriceEntry"/>, so "we looked and found nothing" is a cached answer rather than an
/// absent key.
/// </remarks>
public sealed class ModelPriceCatalog : IModelPriceCatalog
{
    private readonly PriceCatalogRepository _repository;
    private readonly ILogger<ModelPriceCatalog> _logger;

    // Entries hold the raw row plus its fetch timestamp, never a tier-selected price: the same cached row
    // has to answer for every PriceContext a caller might ask about, and freshness is evaluated per read
    // against the timestamp rather than baked in when the entry was filled.
    private ConcurrentDictionary<ModelKey, CatalogPriceEntry?> _cache = new();

    /// <param name="repository">The catalog repository this instance reads rows from on a cache miss.</param>
    /// <param name="logger">
    /// Records a cache-miss read that failed against the repository - see <see cref="GetEntry"/>'s remarks
    /// for why that is swallowed rather than propagated.
    /// </param>
    public ModelPriceCatalog(PriceCatalogRepository repository, ILogger<ModelPriceCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public ModelPrice? GetBestPriceForModel(ModelKey key, PriceContext context) =>
        GetEntry(key) is { } entry ? ApplyTier(entry.Price, context) : null;

    /// <inheritdoc />
    public ModelPrice? GetFreshPriceForRouting(ModelKey key, PriceContext context, TimeSpan maxAge)
    {
        if (GetEntry(key) is not { } entry)
        {
            return null;
        }

        return DateTimeOffset.UtcNow - entry.LastUpdatedUtc > maxAge
            ? null
            : ApplyTier(entry.Price, context);
    }

    /// <inheritdoc />
    public void Invalidate() =>
        Interlocked.Exchange(ref _cache, new ConcurrentDictionary<ModelKey, CatalogPriceEntry?>());

    /// <summary>
    /// Returns the cached row for <paramref name="key"/>, reading through to the repository on a miss.
    /// </summary>
    /// <remarks>
    /// A concurrent <see cref="Invalidate"/> can race this: a read that missed may write its now-superseded
    /// row into the replaced dictionary, or into the old one that is about to be dropped. Both outcomes are
    /// harmless - the value is a price that was true moments ago, and the next invalidation clears it - so
    /// this deliberately does not lock. Serializing every price read behind a lock to close a window that
    /// yields a marginally stale rate would cost far more than the staleness it prevents.
    /// <para>
    /// Uses the <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,TValue)"/> value overload rather
    /// than a separate <c>TryGetValue</c>/<c>TryAdd</c> pair, so a caller always gets back the dictionary's
    /// own value for the key - not a locally-fetched one that lost the add race and may already be stale
    /// relative to what another thread just inserted.
    /// </para>
    /// <para>
    /// A read-through that fails (e.g. the SQLite file is locked or missing) is caught and logged rather
    /// than propagated, and deliberately left uncached: <see cref="IModelPriceCatalog"/> promises callers
    /// on a live request path that a read never throws, so a transient storage fault degrades to "unpriced"
    /// for this one call instead of taking the caller down, and the next call gets a fresh attempt rather
    /// than being stuck behind a cached failure.
    /// </para>
    /// </remarks>
    private CatalogPriceEntry? GetEntry(ModelKey key)
    {
        var cache = _cache;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        CatalogPriceEntry? entry;
        try
        {
            entry = _repository.GetPriceEntry(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Price catalog read failed for {ModelName}/{Provider}; treating this read as unpriced.",
                key.ModelName,
                key.Provider);
            return null;
        }

        return cache.GetOrAdd(key, entry);
    }

    /// <summary>
    /// Rewrites a row's headline input/output rates to the tier <paramref name="context"/> selects, leaving
    /// every other rate on the record untouched.
    /// </summary>
    /// <remarks>
    /// Each flag falls back to the standard rate when the provider publishes no rate for that tier, because
    /// a null tier column means <em>this provider does not offer this</em> rather than <em>this is free</em>
    /// (D7). Falling back therefore overestimates rather than under-reports, which is the only safe
    /// direction for budget enforcement.
    /// <para>
    /// The cache-read rate is applied to the headline <em>input</em> rate only for
    /// <see cref="PriceContext.RepeatsCachedContext"/>, which is an a-priori projection flag - see that
    /// member's remarks for why a caller holding real reported usage must not set it. The record's own
    /// <see cref="ModelPrice.CacheReadPerMillionTokens"/>/<see cref="ModelPrice.CacheWritePerMillionTokens"/>
    /// are passed through unchanged either way, so
    /// <see cref="ModelPrice.EstimateCost(UsageInfo)"/> keeps pricing actual cache tokens from the row's own
    /// rates.
    /// </para>
    /// </remarks>
    private static ModelPrice ApplyTier(ModelPrice price, PriceContext context)
    {
        if (context == PriceContext.Standard)
        {
            return price;
        }

        var input = price.InputPerMillionTokens;
        var output = price.OutputPerMillionTokens;

        if (context.IsBatchRequest)
        {
            input = price.BatchInputPerMillionTokens ?? input;
            output = price.BatchOutputPerMillionTokens ?? output;
        }

        // Applied after the batch tier so a request that is both batched and cache-repeating is priced at
        // the cache rate on input: the two discounts are not additive, and the cached rate is the one that
        // actually describes those input tokens.
        if (context.RepeatsCachedContext)
        {
            input = price.CacheReadPerMillionTokens ?? input;
        }

        return price with
        {
            InputPerMillionTokens = input,
            OutputPerMillionTokens = output
        };
    }
}
