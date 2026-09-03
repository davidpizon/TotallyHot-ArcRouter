namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// How much to trust a request's estimated cost. Exists because the several ways a cost can be absent or
/// approximate are materially different answers that <see langword="null"/> alone flattens into one - and
/// because an aggregate that silently sums only the priced subset is misleading in a way a single request's
/// null is not (see <c>docs/router/token-tracking-improvements.md</c> §5.6).
/// </summary>
public enum CostConfidence
{
    /// <summary>Usage could not be extracted, so nothing was priced. Cost is <see langword="null"/>.</summary>
    NoUsage,

    /// <summary>No fresh catalog price for this (model, provider) cell. Cost is <see langword="null"/>.</summary>
    Unknown,

    /// <summary>
    /// Priced from the catalog, but at least one cache dimension fell back to the standard input rate
    /// because the catalog publishes no cache rate for this cell, or the price itself was resolved via a
    /// resolution-ladder rung below <c>Exact</c> (see <c>docs/router/token-tracking-improvements.md</c>
    /// §5.7) - either way a documented conservative estimate, not an exact figure.
    /// </summary>
    CatalogApproximate,

    /// <summary>Priced from a fresh, exactly-resolved catalog entry with every applicable rate published.</summary>
    Catalog,

    /// <summary>The provider is operator-flagged free, so zero is a known price, not a missing one.</summary>
    Exact
}