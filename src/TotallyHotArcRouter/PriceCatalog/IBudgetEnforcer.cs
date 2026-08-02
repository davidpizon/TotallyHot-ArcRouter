namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// The slice of <see cref="ProviderBudgetStore"/> the request path depends on: the per-request breach check
/// and the spend recording. Kept as its own interface so <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/>
/// depends only on what it uses (not the store's cap-management surface), and so the enforcement seam can be
/// substituted in tests - including the concurrent-edit race where a provider's breach state flips between
/// the pre-loop check and the in-loop skip.
/// </summary>
public interface IBudgetEnforcer
{
    /// <summary>Gets whether the provider has met or exceeded a set monthly cap.</summary>
    bool IsBreached(string providerKey);

    /// <summary>Records one served request's usage against the provider that served it.</summary>
    Task RecordUsageAsync(
        string providerKey,
        decimal? costUsd,
        int? promptTokens,
        int? completionTokens,
        CancellationToken cancellationToken = default);
}

