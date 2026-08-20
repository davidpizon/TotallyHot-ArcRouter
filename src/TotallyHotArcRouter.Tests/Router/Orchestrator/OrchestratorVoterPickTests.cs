using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="OrchestratorRoutingPolicy.TryGetVoterPick"/> - the reader that recovers a single
/// voter's pick from a decision's breakdown, which is how
/// docs/router/self-organizing-classification-plan.md Phase T4 obtains its <c>dim_best</c> counterfactual
/// "requiring no new computation".
/// </summary>
public sealed class OrchestratorVoterPickTests
{
    /// <summary>Builds a decision carrying only the supplied breakdown entries.</summary>
    private static RoutingDecision Decision(params (string Key, double Score)[] entries) =>
        new(
            "kimi-k2.5",
            0.5,
            "test",
            DateTimeOffset.UtcNow,
            entries.ToDictionary(e => e.Key, e => e.Score, StringComparer.OrdinalIgnoreCase),
            isExploratory: false,
            propensity: 1.0);

    [Fact]
    public void TryGetVoterPick_ReturnsThatVotersModel()
    {
        var decision = Decision(
            ("kimi-k2.5", 1.47),
            ("voter:dim_best:glm-5", 0.9),
            ("voter:memory_kNN:kimi-k2.5", 0.57));

        Assert.Equal("glm-5", OrchestratorRoutingPolicy.TryGetVoterPick(decision, VoterNames.DimBest));
    }

    [Fact]
    public void TryGetVoterPick_VoterAbstained_ReturnsNull()
    {
        // An abstaining voter contributes no breakdown entry at all, and must never be answered with the
        // ensemble's own pick - that would manufacture a counterfactual nobody chose.
        var decision = Decision(("kimi-k2.5", 0.57), ("voter:memory_kNN:kimi-k2.5", 0.57));

        Assert.Null(OrchestratorRoutingPolicy.TryGetVoterPick(decision, VoterNames.DimBest));
    }

    [Fact]
    public void TryGetVoterPick_IgnoresOtherVotersEntries()
    {
        var decision = Decision(
            ("voter:logreg:glm-5", 0.43),
            ("voter:cluster_best:minimax-m2.7", 0.5));

        Assert.Null(OrchestratorRoutingPolicy.TryGetVoterPick(decision, VoterNames.DimBest));
    }

    [Fact]
    public void TryGetVoterPick_ModelNameContainingAColon_IsParsedAtTheFirstSeparatorOnly()
    {
        // Provider-prefixed ids are ordinary; the voter name never contains a colon, so the split is at the
        // first one and everything after it is the model.
        var decision = Decision(("voter:dim_best:openrouter:meta-llama/llama-3.1", 0.9));

        Assert.Equal(
            "openrouter:meta-llama/llama-3.1",
            OrchestratorRoutingPolicy.TryGetVoterPick(decision, VoterNames.DimBest));
    }

    [Fact]
    public void TryGetVoterPick_CandidateNamedLikeABreakdownKey_IsNotMistakenForAVote()
    {
        // A real candidate model can legitimately be named "voter:..."; its aggregate-score entry must not
        // be read back as though dim_best had voted for it.
        var decision = Decision(("voter:custom", 1.2));

        Assert.Null(OrchestratorRoutingPolicy.TryGetVoterPick(decision, VoterNames.DimBest));
    }

    [Fact]
    public void TryGetVoterPick_EmptyBreakdown_ReturnsNull()
    {
        Assert.Null(OrchestratorRoutingPolicy.TryGetVoterPick(Decision(), VoterNames.DimBest));
    }
}
