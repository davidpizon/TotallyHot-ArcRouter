namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// How a learning consumer (<see cref="Router.Orchestrator.MemoryKnnVoter"/>, the <c>logreg</c>/cluster
/// trainers, <see cref="Router.Orchestrator.ClusterLedger"/>) treats a <see cref="Router.MemoryEntry"/>
/// whose score did not come from the G-Eval judge (<see cref="Router.MemoryEntry.IsJudgeScored"/> is
/// <see langword="false"/>). A judge-scored row always has weight <c>1.0</c> and this policy does not apply
/// to it, so an unjudged row never counts more than a judge-scored row under any value (issue #326,
/// ADR-0026 Amendment 1). After issue #293 only
/// <see cref="TotallyHot.ArcRouter.Quality.GradeStatus.Complete"/> grades enter live memory (ADR-0026).
/// After issue #319 a surviving row is judge-scored only when <c>JudgeScore</c> has a value, not for every
/// <see cref="TotallyHot.ArcRouter.Quality.GradeStatus.Complete"/> grade.
/// </summary>
public enum UnjudgedRowPolicy
{
    /// <summary>
    /// An unjudged row has weight <c>1.0</c>, the same as a judge-scored row. The default after issue #293
    /// (ADR-0026).
    /// </summary>
    Include,

    /// <summary>
    /// An unjudged row is dropped from training, retrieval, and the cluster ledger entirely. The router
    /// learns from judge-scored rows only.
    /// </summary>
    Exclude,

    /// <summary>
    /// An unjudged row's contribution is scaled by <see cref="RoutingOptions.UnjudgedRowWeight"/> when that
    /// weight is positive, and the row is dropped when it is 0 or less. The weight is at most <c>1</c>, so
    /// the scaled row never counts more than a judge-scored row. Kept as an operator knob, not the default.
    /// <see cref="Router.Orchestrator.ClusterLedger"/> counts every row this policy keeps, scaled or not,
    /// as one observation.
    /// </summary>
    DownWeight
}
