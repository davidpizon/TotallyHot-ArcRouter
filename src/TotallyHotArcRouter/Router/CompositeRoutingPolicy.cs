using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// The <see cref="IRoutingPolicy"/> registered for live traffic: dispatches to
/// <see cref="UtilityRoutingPolicy"/> for requests PLAN.md Phase H's classifier marked
/// <see cref="RoutingContext.IsUtility"/> (<c>docs/router/utility-model-routing.md</c> §B3's tier
/// split), and otherwise to <see cref="Orchestrator.OrchestratorRoutingPolicy"/> - the PLAN.md Phase M
/// default - or <see cref="AgentRouterPolicy"/>'s memory-only ranking when
/// <see cref="RoutingOptions.EnableOrchestratorPolicy"/> is <see langword="false"/>
/// (docs/router/orchestrator-live-path-plan.md M1.1/M1.3).
/// </summary>
/// <remarks>
/// An explicitly-named, servable model never reaches this class: <see cref="Proxy.RequestInterceptor"/>
/// serves it directly and only consults a policy on the paths it already owns (<c>auto</c>, an unknown
/// or disabled model name, a circuit-open primary, the failover cascade) - see
/// docs/router/orchestrator-live-path-plan.md §1. <see cref="UtilityRoutingPolicy"/> keeps its cost term
/// regardless of the kill switch above; only the general (non-utility) leg is affected, since the
/// Orchestrator has no cost term of its own.
/// </remarks>
public sealed class CompositeRoutingPolicy : IRoutingPolicy
{
    private readonly AgentRouterPolicy _generalPolicy;
    private readonly RoutingOptions _options;
    private readonly OrchestratorRoutingPolicy _orchestratorPolicy;
    private readonly UtilityRoutingPolicy _utilityPolicy;

    /// <param name="utilityPolicy">Handles requests classified as utility traffic.</param>
    /// <param name="generalPolicy">
    /// The pre-Phase-M general engine, kept reachable behind <see cref="RoutingOptions.EnableOrchestratorPolicy"/>
    /// as a rollback and as a PLAN.md Phase N comparison baseline.
    /// </param>
    /// <param name="orchestratorPolicy">The PLAN.md Phase M default general engine.</param>
    /// <param name="options">Supplies <see cref="RoutingOptions.EnableOrchestratorPolicy"/>.</param>
    public CompositeRoutingPolicy(
        UtilityRoutingPolicy utilityPolicy,
        AgentRouterPolicy generalPolicy,
        OrchestratorRoutingPolicy orchestratorPolicy,
        IOptions<RoutingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(utilityPolicy);
        ArgumentNullException.ThrowIfNull(generalPolicy);
        ArgumentNullException.ThrowIfNull(orchestratorPolicy);
        ArgumentNullException.ThrowIfNull(options);

        _utilityPolicy = utilityPolicy;
        _generalPolicy = generalPolicy;
        _orchestratorPolicy = orchestratorPolicy;
        _options = options.Value;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the <see cref="RoutingSignals"/> overload of this method with no signals.
    /// </remarks>
    public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        return SelectModelAsync(context: context, null, cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Forwards <paramref name="signals"/> to <see cref="Orchestrator.OrchestratorRoutingPolicy"/> when
    /// the general leg is chosen, so <c>memory_kNN</c> and <c>logreg</c> can participate in a live
    /// decision instead of abstaining (docs/router/live-feedback-learning-plan.md Phase 2a). Neither
    /// <see cref="UtilityRoutingPolicy"/> nor <see cref="AgentRouterPolicy"/> uses signals - both fall
    /// through to <see cref="IRoutingPolicy"/>'s no-signals default, which simply discards them.
    /// </remarks>
    public Task<string> SelectModelAsync(RoutingContext context, RoutingSignals? signals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.IsUtility)
            return _utilityPolicy.SelectModelAsync(context: context, cancellationToken: cancellationToken);

        return _options.EnableOrchestratorPolicy
            ? _orchestratorPolicy.SelectModelAsync(context: context, signals: signals,
                cancellationToken: cancellationToken)
            : _generalPolicy.SelectModelAsync(context: context, cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Mirrors <see cref="SelectModelAsync(RoutingContext, RoutingSignals?, CancellationToken)"/>'s
    /// dispatch exactly, but preserves each leg's real provenance instead of collapsing to a model name:
    /// the utility and pre-Orchestrator general legs fall through to <see cref="IRoutingPolicy"/>'s
    /// default (non-exploratory, propensity 1.0 - neither has an exploration mechanism), while the
    /// Orchestrator leg forwards to its own <see cref="Orchestrator.OrchestratorRoutingPolicy.DecideOutcomeAsync"/>
    /// override, which reports the real epsilon-greedy provenance.
    /// </remarks>
    public Task<RoutingDecision> DecideOutcomeAsync(RoutingContext context, RoutingSignals? signals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.IsUtility)
            return ((IRoutingPolicy)_utilityPolicy).DecideOutcomeAsync(context: context, signals: signals,
                cancellationToken: cancellationToken);

        return _options.EnableOrchestratorPolicy
            ? _orchestratorPolicy.DecideOutcomeAsync(context: context, signals: signals,
                cancellationToken: cancellationToken)
            : ((IRoutingPolicy)_generalPolicy).DecideOutcomeAsync(context: context, signals: signals,
                cancellationToken: cancellationToken);
    }
}