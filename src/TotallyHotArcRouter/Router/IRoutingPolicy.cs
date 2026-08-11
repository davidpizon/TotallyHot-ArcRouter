namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Selection-only routing: chooses which allowlisted model should serve a request without ever
/// invoking it. This is the seam PLAN.md Phase I's Action leg adds so smart routing stays compatible
/// with the existing streaming reverse-proxy forward (<c>docs/router/utility-model-routing.md</c> §B3) -
/// generation remains <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/>'s job, never a policy's.
/// </summary>
public interface IRoutingPolicy
{
    /// <summary>Chooses a model for the given routing context.</summary>
    /// <param name="context">The candidates and classification signal to select from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The selected candidate's <see cref="RoutingCandidate.ModelName"/>.</returns>
    Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default);
}
