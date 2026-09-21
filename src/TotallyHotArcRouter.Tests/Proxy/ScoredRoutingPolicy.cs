using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Test double for a policy that publishes per-model <see cref="RoutingDecision.CandidateScores"/>
/// the way <see cref="TotallyHot.ArcRouter.Router.Orchestrator.OrchestratorRoutingPolicy"/> does, so
/// failover ranking can be asserted against voter picks rather than <see cref="RouterMemory"/>.
/// Picks the highest-scored remaining candidate from the live menu so a circuit-open model that
/// dropped out of eligibility yields the next voter pick automatically.
/// </summary>
internal sealed class ScoredRoutingPolicy(IReadOnlyDictionary<string, double> scores) : IRoutingPolicy
{
    /// <inheritdoc/>
    public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Pick(context));
    }

    /// <inheritdoc/>
    public Task<RoutingDecision> DecideOutcomeAsync(RoutingContext context, RoutingSignals? signals,
        CancellationToken cancellationToken = default)
    {
        var selected = Pick(context);
        return Task.FromResult(new RoutingDecision(
            selectedModel: selected,
            1,
            rationale: "Scored test policy pick.",
            timestampUtc: DateTimeOffset.UtcNow,
            candidateScores: scores));
    }

    private string Pick(RoutingContext context)
    {
        return context.Candidates
            .OrderByDescending(c => scores.GetValueOrDefault(c.ModelName))
            .ThenBy(keySelector: c => c.ModelName, comparer: StringComparer.Ordinal)
            .First().ModelName;
    }
}
