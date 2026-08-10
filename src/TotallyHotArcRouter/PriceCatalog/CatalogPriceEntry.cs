using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// One catalog row as read from the database: every rate tier it publishes, plus when it was last fetched.
/// The timestamp travels with the price so <see cref="ModelPriceCatalog"/> can answer both the display and
/// the freshness-gated routing question from a single cached entry, rather than caching one entry per age
/// bound and letting them drift apart as time passes.
/// </summary>
/// <param name="Price">Every rate tier this cell publishes, unfiltered by any <see cref="PriceContext"/>.</param>
/// <param name="LastUpdatedUtc">
/// When the owning source last refreshed this row. Compared against D1's freshness floor at read time - not
/// at cache-fill time - so an entry cached while fresh correctly becomes unfit for routing once it ages
/// past the floor, without needing to be evicted first.
/// </param>
public readonly record struct CatalogPriceEntry(ModelPrice Price, DateTimeOffset LastUpdatedUtc);
