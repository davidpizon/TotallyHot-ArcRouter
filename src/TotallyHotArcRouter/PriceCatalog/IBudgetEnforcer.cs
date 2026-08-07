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

    /// <summary>
    /// Records one served request's usage against the provider that served it, for the current month.
    /// </summary>
    /// <param name="providerKey">The provider that served the request.</param>
    /// <param name="costUsd">The estimated USD cost, or <see langword="null"/> if unknown (contributes zero).</param>
    /// <param name="promptTokens">Standard prompt/input tokens, or <see langword="null"/> if unknown.</param>
    /// <param name="completionTokens">Completion/output tokens, or <see langword="null"/> if unknown.</param>
    /// <param name="cacheCreationTokens">Cache-creation (cache-write) input tokens, or <see langword="null"/> if unknown.</param>
    /// <param name="cacheReadTokens">Cache-read input tokens, or <see langword="null"/> if unknown.</param>
    /// <param name="usageAtUtc">The UTC instant this usage was recorded (the request's completion time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordUsageAsync(
        string providerKey,
        decimal? costUsd,
        int? promptTokens,
        int? completionTokens,
        int? cacheCreationTokens,
        int? cacheReadTokens,
        DateTimeOffset usageAtUtc,
        CancellationToken cancellationToken = default);
}
