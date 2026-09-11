namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Resolves a <see cref="Router.MemoryEntry"/>'s effective weight under a <see cref="JudgeRowPolicy"/> -
/// the one rule every learning consumer named in docs/router/geval-shadow-scoring-plan.md's G3 "still
/// owed" item (<see cref="Router.Orchestrator.MemoryKnnVoter"/>, the <c>logreg</c>/cluster trainers,
/// <see cref="Router.Orchestrator.ClusterLedger"/>) applies identically, so the policy cannot silently mean
/// something different in one consumer than another.
/// </summary>
public static class JudgeRowWeighting
{
    /// <summary>
    /// Resolves the multiplier a caller should apply to a row's contribution (similarity, sample weight,
    /// or score-sum term).
    /// </summary>
    /// <param name="isJudgeScored">Whether the row's score came from the G-Eval judge.</param>
    /// <param name="policy">The configured policy for judge-scored rows.</param>
    /// <param name="judgeRowWeight">
    /// The multiplier <see cref="JudgeRowPolicy.DownWeight"/> applies; ignored for the other two policies.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the row must be dropped entirely (<see cref="JudgeRowPolicy.Exclude"/>
    /// on a judge-scored row); otherwise the multiplier to apply - always <c>1.0</c> for a row that is not
    /// judge-scored, regardless of policy.
    /// </returns>
    public static double? ResolveWeight(bool isJudgeScored, JudgeRowPolicy policy, double judgeRowWeight)
    {
        if (!isJudgeScored) return 1.0;

        return policy switch
        {
            JudgeRowPolicy.Include => 1.0,
            JudgeRowPolicy.Exclude => null,
            JudgeRowPolicy.DownWeight => judgeRowWeight,
            _ => 1.0
        };
    }
}
