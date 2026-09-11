namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// How a learning consumer (<see cref="Router.Orchestrator.MemoryKnnVoter"/>, the <c>logreg</c>/cluster
/// trainers, <see cref="Router.Orchestrator.ClusterLedger"/>) treats a <see cref="Router.MemoryEntry"/>
/// whose score came from the G-Eval judge (<see cref="Router.MemoryEntry.IsJudgeScored"/>) rather than from
/// <c>QualityScorer</c>'s static signals alone (docs/router/geval-shadow-scoring-plan.md's G3 "still owed"
/// item: this policy was promised in the same phase that promoted the judge to a co-grader, and did not
/// land until now).
/// </summary>
public enum JudgeRowPolicy
{
    /// <summary>A judge-scored row is treated exactly like any other row.</summary>
    Include,

    /// <summary>A judge-scored row is dropped from training/retrieval entirely.</summary>
    Exclude,

    /// <summary>
    /// A judge-scored row's contribution is scaled by <see cref="RoutingOptions.JudgeScoredRowWeight"/>
    /// rather than dropped or trusted at full strength - the default, reflecting that an LLM judge's
    /// opinion is evidence, not ground truth (see G-Eval's own self-preference-bias analysis, cited in
    /// docs/router/geval-shadow-scoring-plan.md's "Why" section).
    /// </summary>
    DownWeight
}
