namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Observes a scored <see cref="QualityResult"/> into the router's learning memory. The host application
/// provides the concrete adapter (writing to a separate live-namespace store); this library only depends
/// on this seam so it never references the core router directly.
/// <para>
/// Called exactly once per request by <see cref="IQualityScoreAggregator"/> - never once per grader. An
/// implementation may assume it is seeing a request's final score.
/// </para>
/// </summary>
public interface IQualityScoreObserver
{
    /// <summary>Observes a scored result.</summary>
    /// <param name="result">The scored result.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the observation is recorded.</returns>
    Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default);
}

