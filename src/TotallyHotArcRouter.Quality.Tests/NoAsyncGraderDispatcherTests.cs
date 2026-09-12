using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers <see cref="NoAsyncGraderDispatcher"/>, the default <see cref="IAsyncGraderDispatcher"/> used
/// when the host application hasn't supplied its own - it must dispatch nothing, so every held result
/// falls back to <see cref="QualityScoreAggregator"/>'s join timeout (or an earlier abstain) rather than
/// waiting on a grader that was never actually started.
/// </summary>
public class NoAsyncGraderDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_AlwaysReturnsAnEmptySet()
    {
        IAsyncGraderDispatcher dispatcher = new NoAsyncGraderDispatcher();
        var result = new QualityResult { Dimension = "code_generation", Model = "test-model" };
        var pendingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "judge", "codejudge" };

        var accepted = await dispatcher.DispatchAsync(result: result, pendingGraderKeys: pendingKeys,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(accepted);
    }
}
