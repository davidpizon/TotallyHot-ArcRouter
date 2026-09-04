using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Narrow, request-path seam for looking up a model's current catalog price. Abstracts the SQLite-backed
/// <see cref="PriceRepository"/> (and its freshness policy) away from
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/>, so the per-request cost path depends only on this one
/// method and can be unit-tested without standing up a real catalog database.
/// </summary>
public interface IModelPriceLookup
{
    /// <summary>
    /// Returns the current per-token price for <paramref name="key"/>, or <see langword="null"/> when the
    /// catalog has no fresh price for it (no such row, only stale rows, or the owning source is disabled).
    /// A <see langword="null"/> here means "cost unknown" - a caller must not collapse it into zero, which
    /// is a different (known) answer reserved for free providers.
    /// </summary>
    ModelPrice? TryGetPrice(ModelKey key);
}