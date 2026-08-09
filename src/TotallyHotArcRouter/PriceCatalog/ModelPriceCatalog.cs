using System.Collections.Concurrent;
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

    // Entries hold the raw row plus its fetch timestamp, never a tier-selected price: the same cached row
    // has to answer for every PriceContext a caller might ask about, and freshness is evaluated per read
    // against the timestamp rather than baked in when the entry was filled.
    private ConcurrentDictionary<ModelKey, CatalogPriceEntry?> _cache = new();

    /// <param name="repository">The catalog repository this instance reads rows from on a cache miss.</param>
    public ModelPriceCatalog(PriceCatalogRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
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
    public void Invalidate() => _cache = new ConcurrentDictionary<ModelKey, CatalogPriceEntry?>();

    /// <summary>
    /// Returns the cached row for <paramref name="key"/>, reading through to the repository on a miss.
    /// </summary>
    /// <remarks>
    /// A concurrent <see cref="Invalidate"/> can race this: a read that missed may write its now-superseded
    /// row into the replaced dictionary, or into the old one that is about to be dropped. Both outcomes are
    /// harmless - the value is a price that was true moments ago, and the next invalidation clears it - so
    /// this deliberately does not lock. Serializing every price read behind a lock to close a window that
    /// yields a marginally stale rate would cost far more than the staleness it prevents.
    /// </remarks>
    private CatalogPriceEntry? GetEntry(ModelKey key)
    {
        var cache = _cache;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var entry = _repository.GetPriceEntry(key);
        cache.TryAdd(key, entry);
        return entry;
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
