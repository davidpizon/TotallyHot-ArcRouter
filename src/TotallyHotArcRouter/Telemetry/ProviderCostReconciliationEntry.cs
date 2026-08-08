namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// One reconciliation snapshot comparing a provider's own reported spend for a closed day against
/// <c>usage_ledger</c>'s local estimate for the same window (§5.8).
/// </summary>
/// <param name="Provider">The provider key (matches <c>ModelRouting:Providers</c>, e.g. <c>"openai"</c>).</param>
/// <param name="WindowStartUtc">The reconciled window's inclusive start (a UTC day boundary).</param>
/// <param name="WindowEndUtc">The reconciled window's exclusive end (one day after <see cref="WindowStartUtc"/>).</param>
/// <param name="ProviderReportedCostUsd">The provider's own reported cost for this window, summed across every paginated page.</param>
/// <param name="LocalEstimatedCostUsd">This proxy's locally estimated cost for the same window, from <c>usage_ledger</c> via the rollup store.</param>
/// <param name="ScopeNote">
/// Records the org-vs-proxy scope caveat: <paramref name="ProviderReportedCostUsd"/> is organization-wide
/// (every key under the configured Admin API key), while <paramref name="LocalEstimatedCostUsd"/> only
/// reflects traffic this proxy instance routed - a gap can be legitimate, not necessarily a pricing bug.
/// </param>
/// <param name="FetchedAtUtc">When this reconciliation snapshot was captured.</param>
public sealed record ProviderCostReconciliationEntry(
    string Provider,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    decimal ProviderReportedCostUsd,
    decimal LocalEstimatedCostUsd,
    string ScopeNote,
    DateTimeOffset FetchedAtUtc);
