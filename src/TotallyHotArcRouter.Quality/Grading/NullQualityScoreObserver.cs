namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// A no-op observer used as a safe default when the host has not registered a real one. Keeps the verifier
/// self-sufficient (e.g. in tests) without silently coupling to router memory.
/// </summary>
public sealed class NullQualityScoreObserver : IQualityScoreObserver
{
    /// <inheritdoc />
    public Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

