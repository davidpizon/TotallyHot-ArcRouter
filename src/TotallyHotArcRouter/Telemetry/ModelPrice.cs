namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// USD price per 1,000,000 tokens for one model.
/// </summary>
/// <remarks>
/// There is no hand-maintained price table: a model's price comes from the auto-refreshed price catalog
/// (see <c>docs/router/model-price-catalog.md</c>), which the proxy consults per request through
/// <c>IModelPriceLookup</c> (see <c>ProxyMiddleware</c>). A free provider the operator flagged
/// <c>ProviderOptions.IsFree</c> has a known price of <see cref="Free"/>; a paid model is priced from the
/// catalog when it holds a fresh entry for it, and is reported as unknown (<see langword="null"/>) - never
/// estimated from unverified rates - when it does not.
/// </remarks>
/// <param name="InputPerMillionTokens">USD per 1,000,000 prompt/input tokens.</param>
/// <param name="OutputPerMillionTokens">USD per 1,000,000 completion/output tokens.</param>
/// <param name="CacheReadPerMillionTokens">
/// USD per 1,000,000 cache-read input tokens, or <see langword="null"/> when the catalog has no published
/// rate for this (model, provider) cell - "unpublished", not "free".
/// </param>
/// <param name="CacheWritePerMillionTokens">
/// USD per 1,000,000 cache-creation (cache-write) input tokens, or <see langword="null"/> when the catalog
/// has no published rate for this (model, provider) cell.
/// </param>
/// <param name="IsApproximateMatch">
/// <see langword="true"/> when this price was resolved via a resolution-ladder rung below
/// <c>ResolutionRung.Exact</c> (see <c>docs/router/token-tracking-improvements.md</c> §5.7) - a stripped
/// snapshot suffix, a normalized version/tier suffix, or a provider-alias match. Such a price is a
/// disclosed estimate, not a confidently exact one: a caller uses this to report
/// <see cref="Telemetry.CostConfidence.CatalogApproximate"/> instead of
/// <see cref="Telemetry.CostConfidence.Catalog"/>.
/// </param>
/// <param name="BatchInputPerMillionTokens">
/// USD per 1,000,000 input tokens at batch/off-peak rates, or <see langword="null"/> when this
/// (model, provider) cell publishes no batch rate. Null means <em>this provider does not offer batch
/// pricing for this model</em> - never that batch is free - so a caller that cannot find a batch rate
/// pays the standard one rather than treating the absence as a discount (D7).
/// </param>
/// <param name="BatchOutputPerMillionTokens">
/// USD per 1,000,000 output tokens at batch/off-peak rates, with the same null semantics as
/// <see cref="BatchInputPerMillionTokens"/>. Kept as its own column rather than derived from the input
/// rate: the two are discounted independently by the providers that publish them.
/// </param>
public sealed record ModelPrice(
    decimal InputPerMillionTokens,
    decimal OutputPerMillionTokens,
    decimal? CacheReadPerMillionTokens = null,
    decimal? CacheWritePerMillionTokens = null,
    bool IsApproximateMatch = false,
    decimal? BatchInputPerMillionTokens = null,
    decimal? BatchOutputPerMillionTokens = null)
{
    /// <summary>
    /// Gets the price of a model served by a free provider: zero, at any token count and at every rate
    /// tier. This is a known price, not a missing one - a local runtime genuinely costs nothing, which is a
    /// fact about the deployment rather than an estimate. Every tier is an explicit <c>0m</c> rather than
    /// <see langword="null"/>, because null carries the opposite meaning everywhere else in this type
    /// ("this provider does not publish this rate"), and a free provider offers every tier at zero.
    /// </summary>
    public static ModelPrice Free { get; } = new(
        InputPerMillionTokens: 0m,
        OutputPerMillionTokens: 0m,
        CacheReadPerMillionTokens: 0m,
        CacheWritePerMillionTokens: 0m,
        IsApproximateMatch: false,
        BatchInputPerMillionTokens: 0m,
        BatchOutputPerMillionTokens: 0m);

    /// <summary>
    /// Estimates the USD cost of a request given only its standard prompt and completion tokens. Kept for
    /// callers with no cache-token data; behaviorally identical to calling <see cref="EstimateCost(UsageInfo)"/>
    /// with a <see cref="UsageInfo"/> whose cache fields are 0.
    /// </summary>
    public decimal EstimateCost(int promptTokens, int completionTokens) =>
        (promptTokens / 1_000_000m * InputPerMillionTokens) + (completionTokens / 1_000_000m * OutputPerMillionTokens);

    /// <summary>
    /// Estimates the USD cost of a request given its full, cache-aware token usage. Standard prompt and
    /// completion tokens are priced at their catalog rate. Cache tokens are priced at their own catalog rate
    /// when the catalog publishes one; when it does not (<see cref="CacheReadPerMillionTokens"/> or
    /// <see cref="CacheWritePerMillionTokens"/> is <see langword="null"/>), those tokens fall back to the
    /// standard input rate - a deliberate, documented conservative overestimate (a real cache read typically
    /// costs roughly 10% of standard input), chosen because overestimating keeps budget enforcement safe,
    /// while hardcoding a provider's cache-discount multiplier would recreate the hand-maintained price table
    /// this type's <c>remarks</c> explicitly refuses. Behaviorally identical to
    /// <see cref="EstimateCost(UsageInfo, out bool)"/> for callers that don't need the fallback flag.
    /// </summary>
    public decimal EstimateCost(UsageInfo usage) => EstimateCost(usage, out _);

    /// <summary>
    /// Estimates the USD cost of a request given its full, cache-aware token usage, additionally reporting
    /// whether a cache dimension with actual tokens fell back to the standard input rate because the
    /// catalog publishes no cache rate for this cell. A caller uses this to distinguish
    /// <see cref="Telemetry.CostConfidence.Catalog"/> (every applicable rate published) from
    /// <see cref="Telemetry.CostConfidence.CatalogApproximate"/> (a fallback fired) - see
    /// <c>docs/router/token-tracking-improvements.md</c> §5.6.
    /// </summary>
    /// <param name="usage">The request's token usage.</param>
    /// <param name="usedCacheRateFallback">
    /// <see langword="true"/> when a nonzero cache-read or cache-creation token count was priced at the
    /// standard input rate because the catalog has no published cache rate for this cell.
    /// </param>
    public decimal EstimateCost(UsageInfo usage, out bool usedCacheRateFallback)
    {
        var cacheReadFellBack = usage.CacheReadTokens > 0 && CacheReadPerMillionTokens is null;
        var cacheWriteFellBack = usage.CacheCreationTokens > 0 && CacheWritePerMillionTokens is null;
        usedCacheRateFallback = cacheReadFellBack || cacheWriteFellBack;

        var cacheReadRate = CacheReadPerMillionTokens ?? InputPerMillionTokens;
        var cacheWriteRate = CacheWritePerMillionTokens ?? InputPerMillionTokens;

        return (usage.PromptTokens / 1_000_000m * InputPerMillionTokens)
            + (usage.CompletionTokens / 1_000_000m * OutputPerMillionTokens)
            + (usage.CacheReadTokens / 1_000_000m * cacheReadRate)
            + (usage.CacheCreationTokens / 1_000_000m * cacheWriteRate);
    }
}
