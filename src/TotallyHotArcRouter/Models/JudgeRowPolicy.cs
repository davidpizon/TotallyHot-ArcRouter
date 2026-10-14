namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// How a learning consumer (<see cref="Router.Orchestrator.MemoryKnnVoter"/>, the <c>logreg</c>/cluster
/// trainers, <see cref="Router.Orchestrator.ClusterLedger"/>) treats a <see cref="Router.MemoryEntry"/>
/// whose score came from the G-Eval judge (<see cref="Router.MemoryEntry.IsJudgeScored"/>) rather than from
/// execution or heuristic signals alone. After issue #293 only
/// <see cref="TotallyHot.ArcRouter.Quality.GradeStatus.Complete"/> grades enter live memory, and every
/// such row is judge-scored by construction (ADR-0026).
/// </summary>
public enum JudgeRowPolicy
{
    /// <summary>
    /// A judge-scored row is treated exactly like any other row. The default after issue #293, because
    /// only question-aware complete grades survive the learning gate.
    /// </summary>
    Include,

    /// <summary>A judge-scored row is dropped from training/retrieval entirely.</summary>
    Exclude,

    /// <summary>
    /// A judge-scored row's contribution is scaled by <see cref="RoutingOptions.JudgeScoredRowWeight"/>
    /// rather than dropped or trusted at full strength. Kept as an operator knob, but no longer the default
    /// once live memory admits only complete judge-grounded grades.
    /// </summary>
    DownWeight
}
