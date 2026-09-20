using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Pins the canonical reward and the live estimated-regret helper
/// (<c>docs/score-delta-methodology.md</c>). These are the formulas the Cost Analytics report card
/// and the offline harness both cite; a silent change here would make the published methodology a
/// lie.
/// </summary>
public sealed class RewardWeightsTests
{
    [Fact]
    public void Canonical_IsThePaperDefaultPair()
    {
        Assert.Equal(expected: 1.0, actual: RewardWeights.Canonical.ScoreWeight);
        Assert.Equal(expected: -0.1, actual: RewardWeights.Canonical.CostWeight);
    }

    [Fact]
    public void ComputeReward_IsEpsilon1TimesScorePlusEpsilon2TimesCost()
    {
        var reward = RewardWeights.ComputeReward(score: 0.90, costUsd: 0.01, epsilon1: 1.0, epsilon2: -0.1);

        Assert.Equal(expected: 0.899, actual: reward, precision: 12);
    }

    [Fact]
    public void ComputeEstimatedRegret_IsBaselineRewardMinusRoutedReward()
    {
        // Worked example from docs/score-delta-methodology.md: routed 0.90 @ $0.01 vs frozen 0.50 @ $0.10.
        var regret = RewardWeights.ComputeEstimatedRegret(
            observedScore: 0.90,
            actualCostUsd: 0.01,
            baselinePredictedScore: 0.50,
            baselineCostUsd: 0.10,
            epsilon1: 1.0,
            epsilon2: -0.1);

        Assert.Equal(expected: -0.409, actual: regret, precision: 12);
    }

    [Fact]
    public void ComputeEstimatedRegret_PositiveWhenTheFrozenPolicyWouldHaveEarnedMore()
    {
        var regret = RewardWeights.ComputeEstimatedRegret(
            observedScore: 0.40,
            actualCostUsd: 0.20,
            baselinePredictedScore: 0.80,
            baselineCostUsd: 0.05,
            epsilon1: 1.0,
            epsilon2: -0.1);

        Assert.True(regret > 0, userMessage: $"Router lost on both axes; regret should be positive, got {regret}.");
    }

    [Fact]
    public void ComputeEstimatedRegret_EqualsNegativeScoreDeltaTimesEpsilon1PlusSavingsTimesEpsilon2()
    {
        const double observed = 0.90;
        const double actualCost = 0.01;
        const double baseline = 0.50;
        const double baselineCost = 0.10;
        const double epsilon1 = 1.0;
        const double epsilon2 = -0.1;

        var regret = RewardWeights.ComputeEstimatedRegret(
            observedScore: observed,
            actualCostUsd: actualCost,
            baselinePredictedScore: baseline,
            baselineCostUsd: baselineCost,
            epsilon1: epsilon1,
            epsilon2: epsilon2);

        var scoreDelta = observed - baseline;
        var savings = baselineCost - actualCost;
        var fromIdentity = -epsilon1 * scoreDelta + epsilon2 * savings;

        Assert.Equal(expected: fromIdentity, actual: regret, precision: 12);
    }

    [Fact]
    public void InstanceReward_MatchesComputeRewardOnTheSameCell()
    {
        var cell = new RegretOutcomeCell(Score: 0.8, CostUsd: 0.02, TotalTokens: 1000);

        Assert.Equal(
            expected: RewardWeights.ComputeReward(score: 0.8, costUsd: 0.02, epsilon1: 1.0, epsilon2: -0.1),
            actual: RewardWeights.Canonical.Reward(cell),
            precision: 12);
    }
}
