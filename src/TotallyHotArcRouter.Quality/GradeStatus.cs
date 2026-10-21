namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Whether a <see cref="QualityResult"/> measured the question well enough to train the router
/// (issue #293 / ADR-0026). Only <see cref="Complete"/> grades enter live learning memory.
/// </summary>
public enum GradeStatus
{
    /// <summary>
    /// Every grader with a nonzero weight for this dimension produced a score, the question was present,
    /// and syntax confidence met the complete-grade threshold. Safe to learn from.
    /// </summary>
    Complete = 0,

    /// <summary>
    /// No judge (and no other weighted LLM grader) was available; only structure signals scored. Stored
    /// for the dashboard, never learned from.
    /// </summary>
    StructureOnly = 1,

    /// <summary>
    /// A weighted grader was available but abstained, timed out, or failed; or syntax confidence was too
    /// low; or the question was missing. Stored for audit, never learned from.
    /// </summary>
    Incomplete = 2
}
