namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The cost-aware reward weights <c>(ε1, ε2)</c> in <c>r = ε1·s + ε2·κ</c>
/// (docs/router/regret-evaluation-harness-plan.md, research-doc §A.2), applied identically to every
/// baseline and the Orchestrator arm so their <see cref="RegretReplayResult"/> numbers are comparable.
/// The same static helpers compute live <c>taxonomy_comparisons.estimated_regret</c> — the full
/// reward difference, not the quality-only score-delta
/// (<c>observedScore − baselinePredictedScore</c>, which is not stored); see
/// <c>docs/score-delta-methodology.md</c>.
/// </summary>
/// <param name="ScoreWeight">
/// ε1, the weight on the verifier score <c>s_ij ∈ [0,1]</c>. Canonical value <c>1</c>.
/// </param>
/// <param name="CostWeight">
/// ε2, the weight on cost <c>κ_ij</c> (<see cref="RegretOutcomeCell.CostUsd"/>). Canonical value
/// <c>-0.1</c>, so a higher cost lowers the reward.
/// </param>
public sealed record RewardWeights(double ScoreWeight, double CostWeight)
{
    /// <summary>
    /// The canonical weights from research-doc §A.2: <c>ε1 = 1</c>, <c>ε2 = -0.1</c>.
    /// </summary>
    public static RewardWeights Canonical { get; } = new(1d, -0.1d);

    /// <summary>
    /// Computes the per-decision reward <c>r = ε1·s + ε2·κ</c>. The live comparison and the offline
    /// harness both call this so they share the algebra; they do not necessarily share the same
    /// <paramref name="epsilon1"/>/<paramref name="epsilon2"/> (live reads
    /// <c>RoutingOptions</c>, the harness uses <see cref="Canonical"/>).
    /// </summary>
    /// <param name="score">Verifier score <c>s</c>, typically in <c>[0, 1]</c>.</param>
    /// <param name="costUsd">Monetary cost <c>κ</c> in USD.</param>
    /// <param name="epsilon1">Quality weight ε1. Canonical value <c>1</c>.</param>
    /// <param name="epsilon2">Cost weight ε2. Canonical value <c>-0.1</c> (higher cost lowers reward).</param>
    /// <returns>The scalar reward.</returns>
    public static double ComputeReward(double score, double costUsd, double epsilon1, double epsilon2)
    {
        return epsilon1 * score + epsilon2 * costUsd;
    }

    /// <summary>
    /// Computes estimated regret of a routed outcome against a frozen-baseline counterfactual:
    /// <c>(ε1·s_base + ε2·κ_base) − (ε1·s_obs + ε2·κ_obs)</c>. Positive means the baseline would
    /// likely have earned more reward; negative means routing beat it. This is the live figure stored
    /// as <c>taxonomy_comparisons.estimated_regret</c> — see <c>docs/score-delta-methodology.md</c>.
    /// </summary>
    /// <param name="observedScore">Verifier score of the model that actually served the request.</param>
    /// <param name="actualCostUsd">What that request actually cost, in USD.</param>
    /// <param name="baselinePredictedScore">
    /// The frozen probing-prior average for the untrained baseline's pick — predicted, not observed.
    /// </param>
    /// <param name="baselineCostUsd">Estimated cost of the untrained baseline's pick, in USD.</param>
    /// <param name="epsilon1">Quality weight ε1. Canonical value <c>1</c>.</param>
    /// <param name="epsilon2">Cost weight ε2. Canonical value <c>-0.1</c>.</param>
    /// <returns>The estimated regret (baseline reward minus routed reward).</returns>
    public static double ComputeEstimatedRegret(
        double observedScore,
        double actualCostUsd,
        double baselinePredictedScore,
        double baselineCostUsd,
        double epsilon1,
        double epsilon2)
    {
        return ComputeReward(score: baselinePredictedScore, costUsd: baselineCostUsd, epsilon1: epsilon1,
                   epsilon2: epsilon2)
               - ComputeReward(score: observedScore, costUsd: actualCostUsd, epsilon1: epsilon1, epsilon2: epsilon2);
    }

    /// <summary>
    /// Computes <c>r = ε1·s + ε2·κ</c> for one outcome cell, using this instance's weights.
    /// </summary>
    /// <param name="cell">The cell to score.</param>
    public double Reward(RegretOutcomeCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return ComputeReward(score: cell.Score, costUsd: cell.CostUsd, epsilon1: ScoreWeight, epsilon2: CostWeight);
    }
}