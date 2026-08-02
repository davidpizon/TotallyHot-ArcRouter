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
public sealed record ModelPrice(decimal InputPerMillionTokens, decimal OutputPerMillionTokens)
{
    /// <summary>
    /// Gets the price of a model served by a free provider: zero, at any token count. This is a known
    /// price, not a missing one - a local runtime genuinely costs nothing, which is a fact about the
    /// deployment rather than an estimate.
    /// </summary>
    public static ModelPrice Free { get; } = new(0m, 0m);

    /// <summary>
    /// Estimates the USD cost of a request given its token usage.
    /// </summary>
    public decimal EstimateCost(int promptTokens, int completionTokens) =>
        (promptTokens / 1_000_000m * InputPerMillionTokens) + (completionTokens / 1_000_000m * OutputPerMillionTokens);
}

