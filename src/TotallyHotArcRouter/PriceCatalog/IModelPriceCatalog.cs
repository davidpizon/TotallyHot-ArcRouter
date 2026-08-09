using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// The price catalog's read surface: two questions about a <c>(model, provider)</c> cell, each answered at
/// the rate tier a given request is actually billed at. Every method is an in-memory read - it never awaits
/// network I/O and never throws, so a source outage is the ingestion service's problem rather than a
/// caller's, which is what makes reading a price inline with a live request viable at all.
/// </summary>
/// <remarks>
/// This is the wider sibling of <see cref="IModelPriceLookup"/>, which stays as the narrow, single-method
/// seam the per-request telemetry path in <c>ProxyMiddleware</c> depends on. Both are kept: a caller that
/// only ever needs "the standard fresh price for this key" should not have to construct a
/// <see cref="PriceContext"/> it has no opinion about.
/// <para>
/// <b>A free provider's zero does not come from here.</b> <c>ProviderOptions.IsFree</c> is a fact about the
/// deployment, not a fetched row, so it never enters <c>model_prices</c> and both methods return
/// <see langword="null"/> for such a model. An implementation that "helpfully" returned
/// <see cref="ModelPrice.Free"/> here would collapse the known-zero and unknown-price cases that
/// <c>docs/router/model-price-catalog.md</c> D1 exists to keep apart; the carve-out belongs to the caller.
/// </para>
/// </remarks>
public interface IModelPriceCatalog
{
    /// <summary>
    /// Answers "what did this cost?" - the display question. Serves rows at any age, because a stale price
    /// is still the most honest number available for something that already happened, and showing nothing
    /// would be worse.
    /// </summary>
    /// <param name="key">The model-and-provider cell to price.</param>
    /// <param name="context">
    /// Which rate tier to report. A caller with no request in hand (a governance price card) passes
    /// <see cref="PriceContext.Standard"/>.
    /// </param>
    /// <returns>
    /// The tier-selected price, or <see langword="null"/> when the catalog has no row for this key at all.
    /// </returns>
    ModelPrice? GetBestPriceForModel(ModelKey key, PriceContext context);

    /// <summary>
    /// Answers "can I rank this model on cost?" - the decision question, which applies D1's freshness floor
    /// because steering routing with a price that may have moved is worse than declining to steer at all.
    /// Takes the same <paramref name="context"/> as its display sibling so a batch-eligible request ranks
    /// candidates at batch rates rather than silently comparing everything at standard ones.
    /// </summary>
    /// <param name="key">The model-and-provider cell to price.</param>
    /// <param name="context">Which rate tier to report.</param>
    /// <param name="maxAge">
    /// How old the underlying row may be and still be trusted for a decision - D1's 24-hour floor in
    /// practice.
    /// </param>
    /// <returns>
    /// The tier-selected price, or <see langword="null"/> when the newest row is older than
    /// <paramref name="maxAge"/> or absent entirely. Both mean <em>unpriced</em>, and callers must treat
    /// them identically rather than substituting a default rate.
    /// </returns>
    ModelPrice? GetFreshPriceForRouting(ModelKey key, PriceContext context, TimeSpan maxAge);

    /// <summary>
    /// Drops every cached row, so the next read re-reads the database. Called by the ingestion service once
    /// a cycle has actually committed a change - see <c>docs/router/model-price-catalog.md</c> Phase 4's
    /// eviction signal, which is deliberately "a delta was committed" rather than a timer, so an unchanged
    /// refresh does not churn the cache.
    /// </summary>
    void Invalidate();
}
