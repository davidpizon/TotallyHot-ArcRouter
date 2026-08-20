using TotallyHot.ArcRouter.Models;

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

    /// <summary>
    /// Chooses a model for the given routing context and returns the full outcome, including
    /// <see cref="RoutingDecision.IsExploratory"/> and <see cref="RoutingDecision.Propensity"/> -
    /// docs/router/self-organizing-classification-plan.md Phase T1c. The default implementation
    /// delegates to <see cref="SelectModelAsync(RoutingContext, RoutingSignals?, CancellationToken)"/>
    /// and wraps the returned model name in a non-exploratory, certain-propensity decision, so every
    /// existing <see cref="IRoutingPolicy"/> implementation gets correct-enough behavior (no exploration
    /// mechanism means propensity 1.0) without any change.
    /// <see cref="Orchestrator.OrchestratorRoutingPolicy"/> and <see cref="CompositeRoutingPolicy"/>
    /// override this to report their real provenance instead.
    /// </summary>
    /// <param name="context">The candidates and classification signal to select from.</param>
    /// <param name="signals">Out-of-band signals about the request, or <see langword="null"/> if none are available.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The full routing decision, including provenance.</returns>
    async Task<RoutingDecision> DecideOutcomeAsync(RoutingContext context, RoutingSignals? signals, CancellationToken cancellationToken = default)
    {
        var model = await SelectModelAsync(context, signals, cancellationToken).ConfigureAwait(false);
        return new RoutingDecision(
            model,
            confidence: 0,
            rationale: "Wrapped from SelectModelAsync by the IRoutingPolicy.DecideOutcomeAsync default implementation.",
            timestampUtc: DateTimeOffset.UtcNow,
            candidateScores: null,
            isExploratory: false,
            propensity: 1.0);
    }
}
