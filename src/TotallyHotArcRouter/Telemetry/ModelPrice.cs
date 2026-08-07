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
public sealed record ModelPrice(
    decimal InputPerMillionTokens,
    decimal OutputPerMillionTokens,
    decimal? CacheReadPerMillionTokens = null,
    decimal? CacheWritePerMillionTokens = null)
{
    /// <summary>
    /// Gets the price of a model served by a free provider: zero, at any token count. This is a known
    /// price, not a missing one - a local runtime genuinely costs nothing, which is a fact about the
    /// deployment rather than an estimate.
    /// </summary>
    public static ModelPrice Free { get; } = new(0m, 0m, 0m, 0m);

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
    /// this type's <c>remarks</c> explicitly refuses.
    /// </summary>
    public decimal EstimateCost(UsageInfo usage)
    {
        var cacheReadRate = CacheReadPerMillionTokens ?? InputPerMillionTokens;
        var cacheWriteRate = CacheWritePerMillionTokens ?? InputPerMillionTokens;

        return (usage.PromptTokens / 1_000_000m * InputPerMillionTokens)
            + (usage.CompletionTokens / 1_000_000m * OutputPerMillionTokens)
            + (usage.CacheReadTokens / 1_000_000m * cacheReadRate)
            + (usage.CacheCreationTokens / 1_000_000m * cacheWriteRate);
    }
}
