using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers the no-op default <see cref="IQualityScoreObserver"/>.</summary>
public class NullQualityScoreObserverTests
{
    [Fact]
    public async Task ObserveAsync_CompletesWithoutThrowing()
    {
        var observer = new NullQualityScoreObserver();

        await observer.ObserveAsync(result: new QualityResult(),
            cancellationToken: TestContext.Current.CancellationToken);
    }
}