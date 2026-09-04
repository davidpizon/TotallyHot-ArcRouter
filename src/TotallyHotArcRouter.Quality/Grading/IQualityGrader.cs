namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Grades a single <see cref="QualityRequest"/> by static means only - structural parse, static analysis,
/// and scoring - into a populated <see cref="QualityResult"/>.
/// </summary>
public interface IQualityGrader
{
    /// <summary>Grades a request and returns its scored result.</summary>
    /// <param name="request">The request to grade.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The populated result, carrying the static score in <see cref="QualityResult.UnifiedScore"/>. The
    /// judge axis is still empty at this point; <see cref="IQualityScoreAggregator"/> fills it and
    /// rescores before anything reaches router memory.
    /// </returns>
    Task<QualityResult> GradeAsync(QualityRequest request, CancellationToken cancellationToken);
}