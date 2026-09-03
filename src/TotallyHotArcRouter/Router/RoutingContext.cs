namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// The input an <see cref="IRoutingPolicy"/> selects a model from - PLAN.md Phase I's Action leg,
/// <c>docs/router/utility-model-routing.md</c> §B3. Deliberately narrower than <c>PriceCatalog.PriceContext</c>,
/// which is a rate-tier selector the price catalog itself consults and knows nothing about routing.
/// </summary>
/// <param name="Dimension">
/// The request's live <see cref="RouterMemory"/> dimension key (already composed through
/// <see cref="TotallyHot.ArcRouter.Quality.RouterDimension.ToLiveKey"/>), so a policy can read/write
/// <see cref="RouterMemory"/> scores directly without reconstructing the key itself.
/// </param>
/// <param name="IsUtility">
/// Whether PLAN.md Phase H's classifier judged this request lightweight background/auxiliary traffic
/// (<c>docs/router/utility-model-routing.md</c> B2) - the signal <see cref="CompositeRoutingPolicy"/>
/// uses to pick between the cost-aware utility policy and the general engine.
/// </param>
/// <param name="Candidates">
/// Every currently-eligible model (not circuit-open, not disabled) a policy may select from - never
/// empty when a caller has at least one live route.
/// </param>
public sealed record RoutingContext(string Dimension, bool IsUtility, IReadOnlyList<RoutingCandidate> Candidates);