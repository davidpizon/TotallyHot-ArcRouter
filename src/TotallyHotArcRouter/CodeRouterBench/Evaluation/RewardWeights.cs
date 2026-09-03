namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The cost-aware reward weights <c>(ε1, ε2)</c> in <c>r = ε1·s + ε2·κ</c>
/// (docs/router/regret-evaluation-harness-plan.md, research-doc §A.2), applied identically to every
/// baseline and the Orchestrator arm so their <see cref="RegretReplayResult"/> numbers are comparable.
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
    /// Computes <c>r = ε1·s + ε2·κ</c> for one outcome cell.
    /// </summary>
    /// <param name="cell">The cell to score.</param>
    public double Reward(RegretOutcomeCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return ScoreWeight * cell.Score + CostWeight * cell.CostUsd;
    }
}