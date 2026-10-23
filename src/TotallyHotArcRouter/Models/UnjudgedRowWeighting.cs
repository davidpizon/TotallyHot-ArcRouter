namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Resolves a <see cref="Router.MemoryEntry"/>'s effective weight under an <see cref="UnjudgedRowPolicy"/> -
/// the one rule every learning consumer named in docs/router/geval-shadow-scoring-plan.md's G3 "still
/// owed" item (<see cref="Router.Orchestrator.MemoryKnnVoter"/>, the <c>logreg</c>/cluster trainers,
/// <see cref="Router.Orchestrator.ClusterLedger"/>) applies identically, so the policy cannot silently mean
/// something different in one consumer than another. A judge-scored row always resolves to <c>1.0</c>; the
/// policy applies only to unjudged rows, so an unjudged row never outweighs a judge-scored row (issue #326,
/// ADR-0026 Amendment 1).
/// </summary>
public static class UnjudgedRowWeighting
{
    /// <summary>
    /// Resolves the multiplier a caller should apply to a row's contribution (similarity, sample weight,
    /// or score-sum term).
    /// </summary>
    /// <param name="isJudgeScored">Whether the row's score came from the G-Eval judge.</param>
    /// <param name="policy">The configured policy for unjudged rows.</param>
    /// <param name="unjudgedRowWeight">
    /// The multiplier <see cref="UnjudgedRowPolicy.DownWeight"/> applies to an unjudged row; ignored for the
    /// other two policies and for a judge-scored row.
    /// </param>
    /// <returns>
    /// <c>1.0</c> for a judge-scored row, regardless of policy. For an unjudged row, <see langword="null"/>
    /// when the row must be dropped entirely (<see cref="UnjudgedRowPolicy.Exclude"/>, or
    /// <see cref="UnjudgedRowPolicy.DownWeight"/> with a weight of 0 or less); otherwise the multiplier to
    /// apply.
    /// </returns>
    public static double? ResolveWeight(bool isJudgeScored, UnjudgedRowPolicy policy, double unjudgedRowWeight)
    {
        if (isJudgeScored) return 1.0;

        return policy switch
        {
            UnjudgedRowPolicy.Include => 1.0,
            UnjudgedRowPolicy.Exclude => null,
            UnjudgedRowPolicy.DownWeight => unjudgedRowWeight <= 0 ? null : unjudgedRowWeight,
            _ => throw new ArgumentException($"Unknown UnjudgedRowPolicy: {policy}", nameof(policy))
        };
    }
}
