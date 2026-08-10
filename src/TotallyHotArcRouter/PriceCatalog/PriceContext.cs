namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Selects which rate tier - standard, batch, or cached - a price lookup should report. A pricing
/// concern, not a routing one: telemetry and the governance cards both read prices without routing
/// anything, so this is deliberately <em>not</em> <c>utility-model-routing.md</c>'s <c>RoutingContext</c>
/// (that one carries <c>dimension</c> and <c>isUtility</c> for the selection policy, and a governance card
/// has neither). A routing policy builds one of these from its own context; a card passes
/// <see cref="Standard"/>.
/// <para>
/// Deliberately carries no provider - that is identity, and it lives in <see cref="ModelKey"/>. One
/// context describes one <em>request</em>, while the router evaluates many candidate providers against it,
/// so at selection time "the provider" is not yet a fact; it is the thing being chosen (see
/// <c>docs/router/model-price-catalog.md</c> D7).
/// </para>
/// </summary>
/// <param name="IsBatchRequest">
/// The caller accepts a delayed, off-peak turnaround, so <c>batch_*</c> rates apply where the provider
/// publishes them. Where it does not, the standard rate is reported instead - an absent batch rate means
/// "not offered here", so such a request is billed at full price rather than at a discount that does not
/// exist (D7).
/// </param>
/// <param name="RepeatsCachedContext">
/// This request repeats context or instructions a previous one already sent, so <c>cached_input_price</c>
/// applies where the provider publishes it. A fact about the <em>request</em>, not the provider: whether
/// caching is offered at all is already expressed by that rate being null.
/// <para>
/// <b>Only ever set this for an a-priori estimate</b> - ranking candidates before a request is sent, where
/// no reported usage exists yet and the caller prices a projected token count via
/// <see cref="TotallyHot.ArcRouter.Telemetry.ModelPrice.EstimateCost(int, int)"/>. A caller that holds a real
/// <see cref="TotallyHot.ArcRouter.Telemetry.UsageInfo"/> must pass <see cref="Standard"/> instead, because
/// <see cref="TotallyHot.ArcRouter.Telemetry.ModelPrice.EstimateCost(TotallyHot.ArcRouter.Telemetry.UsageInfo)"/>
/// already prices the provider's own reported cache-read and cache-write token counts at their own rates.
/// Setting this flag <em>and</em> passing real usage would discount the same tokens twice and under-report
/// spend, which is the one direction budget enforcement must never err in.
/// </para>
/// </param>
public readonly record struct PriceContext(bool IsBatchRequest, bool RepeatsCachedContext)
{
    /// <summary>
    /// Gets the honest default: full standard rates, no batch discount and no assumed cache reuse. This is
    /// what a caller with no request in hand passes (a governance price card), and what every caller
    /// holding real reported usage passes - see <see cref="RepeatsCachedContext"/> for why the latter must
    /// not do otherwise.
    /// </summary>
    public static PriceContext Standard { get; } = new(false, false);
}
