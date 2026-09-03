using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// <see cref="IModelPriceLookup"/> backed by the <see cref="PriceRepository"/>, applying the same
/// 24-hour freshness floor the startup health check and the catalog's stale-retention policy use (see
/// <c>docs/router/model-price-catalog.md</c>). A price older than that window is treated as unknown rather
/// than served, so a stalled ingestion source can never silently price live requests off month-old data.
/// </summary>
public sealed class PriceCatalogModelPriceLookup : IModelPriceLookup
{
    // Kept in step with StartupHealthCheckHostedService.FreshnessFloor and the catalog's 24h stale-retention
    // window: the same definition of "fresh enough to act on" the rest of the price subsystem already uses.
    private static readonly TimeSpan FreshnessFloor = TimeSpan.FromHours(24);

    private readonly PriceRepository _repository;

    /// <param name="repository">The catalog repository this lookup reads fresh prices from.</param>
    public PriceCatalogModelPriceLookup(PriceRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <inheritdoc/>
    public ModelPrice? TryGetPrice(ModelKey key)
    {
        return _repository.GetFreshPrice(key: key, maxAge: FreshnessFloor);
    }
}