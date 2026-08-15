namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// One signal source in <see cref="OrchestratorRoutingPolicy"/>'s weighted-vote ensemble (PLAN.md Phase L,
/// research-doc §3.3/A.1's "Orchestrator" - <c>dim_best</c>, <c>memory_kNN</c>, <c>logreg</c>,
/// <c>llm_router</c>). A voter never invokes a model and never fails the ensemble: an unavailable model
/// artifact, missing context, or an internal error all resolve to <see cref="VoterVote.Abstain"/> rather
/// than an exception, so <see cref="OrchestratorRoutingPolicy"/> can degrade to fewer voters without ever
/// hard-failing the routing decision.
/// </summary>
public interface IRoutingVoter
{
    /// <summary>This voter's canonical identity - one of the constants on <see cref="VoterNames"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Casts this voter's vote for <paramref name="context"/>'s candidates.
    /// </summary>
    /// <param name="context">The dimension, candidates, and optional task embedding/text to vote from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The picked candidate and this voter's confidence in it, or <see cref="VoterVote.Abstain"/> when the
    /// voter cannot form an opinion (per <see cref="VoterVote.IsAbstain"/>).
    /// </returns>
    Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default);
}
