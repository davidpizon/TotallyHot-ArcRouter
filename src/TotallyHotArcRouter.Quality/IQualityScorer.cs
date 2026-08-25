namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Collapses the syntax, static-analysis, and judge signals of a <see cref="QualityResult"/> into the
/// unified score u_i in [0,1], using per-dimension weights.
/// </summary>
public interface IQualityScorer
{
    /// <summary>Computes u_i for a populated (but not-yet-scored) result under the given dimension's weights.</summary>
    /// <param name="result">The result whose signal fields are populated.</param>
    /// <param name="dimension">The task dimension whose weights apply.</param>
    /// <returns>The unified score in [0,1].</returns>
    double Score(QualityResult result, string dimension);
}
