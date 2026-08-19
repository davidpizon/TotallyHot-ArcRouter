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

    /// <summary>
    /// Chooses a model for the given routing context, with <paramref name="signals"/> (prompt text and/or
    /// embedding) available for policies that can use them - docs/router/live-feedback-learning-plan.md
    /// Phase 2a. The default implementation ignores <paramref name="signals"/> and delegates to
    /// <see cref="SelectModelAsync(RoutingContext, CancellationToken)"/>, so every existing
    /// <see cref="IRoutingPolicy"/> implementation and caller keeps compiling and behaving exactly as
    /// before without opting in. <see cref="Orchestrator.OrchestratorRoutingPolicy"/> overrides this to
    /// actually use the signals; <see cref="CompositeRoutingPolicy"/> also overrides it, purely to
    /// forward to whichever leg it dispatches to (docs/router/orchestrator-live-path-plan.md M1.1) -
    /// <see cref="UtilityRoutingPolicy"/> and <see cref="AgentRouterPolicy"/> still fall through to this
    /// default and discard the signals either way.
    /// </summary>
    /// <param name="context">The candidates and classification signal to select from.</param>
    /// <param name="signals">Out-of-band signals about the request, or <see langword="null"/> if none are available.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The selected candidate's <see cref="RoutingCandidate.ModelName"/>.</returns>
    Task<string> SelectModelAsync(RoutingContext context, RoutingSignals? signals, CancellationToken cancellationToken = default) =>
        SelectModelAsync(context, cancellationToken);
}
