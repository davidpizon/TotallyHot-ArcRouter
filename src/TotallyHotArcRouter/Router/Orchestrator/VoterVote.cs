namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// One <see cref="IRoutingVoter"/>'s vote: the model it picked and how confident it is, or an abstention
/// when the voter had nothing to go on (no model artifact, no embedding/text supplied, no neighbors
/// cleared its threshold). <see cref="OrchestratorRoutingPolicy"/> is built to degrade gracefully when a
/// voter abstains - PLAN.md Phase L's "must degrade to a three-voter vote, never a hard failure" - rather
/// than treating an abstention as an error.
/// </summary>
/// <param name="VoterName">The voting voter's <see cref="IRoutingVoter.Name"/> (see <see cref="VoterNames"/>).</param>
/// <param name="ModelName">
/// The candidate model this voter picked, or <see langword="null"/> when the voter abstained.
/// </param>
/// <param name="Confidence">
/// The voter's own confidence in its pick, in <c>[0, 1]</c>. Meaningless (and always <c>0</c>) on an
/// abstention. <see cref="OrchestratorRoutingPolicy"/> multiplies this by the voter's configured weight to
/// get that voter's contribution to a candidate's weighted score.
/// </param>
public sealed record VoterVote(string VoterName, string? ModelName, double Confidence)
{
    /// <summary>Whether this vote is an abstention - equivalent to <see cref="ModelName"/> being <see langword="null"/>.</summary>
    public bool IsAbstain => ModelName is null;

    /// <summary>Builds an abstaining vote for <paramref name="voterName"/>.</summary>
    /// <param name="voterName">The abstaining voter's <see cref="IRoutingVoter.Name"/>.</param>
    public static VoterVote Abstain(string voterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voterName);
        return new VoterVote(VoterName: voterName, null, 0d);
    }
}