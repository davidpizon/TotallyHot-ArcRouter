namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// How a learning consumer (<see cref="Router.Orchestrator.MemoryKnnVoter"/>, the <c>logreg</c>/cluster
/// trainers, <see cref="Router.Orchestrator.ClusterLedger"/>) treats a <see cref="Router.MemoryEntry"/>
/// whose score came from the G-Eval judge (<see cref="Router.MemoryEntry.IsJudgeScored"/>) rather than from
/// execution or heuristic signals alone. After issue #293 only
/// <see cref="TotallyHot.ArcRouter.Quality.GradeStatus.Complete"/> grades enter live memory (ADR-0026).
/// After issue #319 a surviving row is judge-scored only when <c>JudgeScore</c> has a value, not for every
/// <see cref="TotallyHot.ArcRouter.Quality.GradeStatus.Complete"/> grade.
/// </summary>
public enum JudgeRowPolicy
{
    /// <summary>
    /// A judge-scored row is treated exactly like any other row. The default after issue #293 (ADR-0026).
    /// A row with <c>is_judge_scored = 0</c> keeps multiplier <c>1.0</c>.
    /// <see cref="JudgeRowWeighting.ResolveWeight"/> returns that weight before this policy is consulted,
    /// and this value also returns <c>1.0</c> for a judge-scored row, so a
    /// <see cref="TotallyHot.ArcRouter.Quality.GradeStatus.Complete"/> grade with no <c>JudgeScore</c>
    /// (issue #319) contributes at the same full strength.
    /// </summary>
    Include,

    /// <summary>A judge-scored row is dropped from training/retrieval entirely.</summary>
    Exclude,

    /// <summary>
    /// A judge-scored row's contribution is scaled by <see cref="RoutingOptions.JudgeScoredRowWeight"/>
    /// when that weight is positive, and dropped when it is 0 or less. Kept as an operator knob, and no
    /// longer the default after issue #293. A row with <c>is_judge_scored = 0</c> keeps multiplier
    /// <c>1.0</c>. <see cref="JudgeRowWeighting.ResolveWeight"/> returns that weight before this policy is
    /// consulted. A <see cref="TotallyHot.ArcRouter.Quality.GradeStatus.Complete"/> grade with no
    /// <c>JudgeScore</c> (issue #319) stays at full strength in the weighted mean.
    /// <see cref="Router.Orchestrator.ClusterLedger"/> counts every row this policy keeps, scaled or not,
    /// as one observation.
    /// </summary>
    DownWeight
}
