namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// One currently-eligible model an <see cref="IRoutingPolicy"/> may select, carrying just enough of its
/// <see cref="TotallyHot.ArcRouter.Proxy.ResolvedModelRoute"/> for cost lookup - a price is keyed on
/// <c>(model, provider)</c>, not on the model name alone (<c>docs/router/model-price-catalog.md</c>'s D7).
/// </summary>
/// <param name="ModelName">The client-facing model name, matching a live <c>ModelList</c> entry.</param>
/// <param name="Provider">The <c>ModelRouting:Providers</c> key this candidate currently routes to.</param>
/// <param name="IsFree">
/// Whether the provider is flagged <c>ProviderOptions.IsFree</c> - a known-zero cost that is exempt from
/// the price catalog's 24h freshness floor (<c>docs/router/utility-model-routing.md</c> §B3.2).
/// </param>
public sealed record RoutingCandidate(string ModelName, string Provider, bool IsFree);
