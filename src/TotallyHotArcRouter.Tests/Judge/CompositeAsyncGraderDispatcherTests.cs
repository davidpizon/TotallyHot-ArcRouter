using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="CompositeAsyncGraderDispatcher"/>: it must union every component dispatcher's accepted
/// keys, and one dispatcher's failure must not prevent the others from being tried or from contributing
/// their own accepted keys.
/// </summary>
public class CompositeAsyncGraderDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_UnionsAcceptedKeysAcrossDispatchers()
    {
        var composite = new CompositeAsyncGraderDispatcher(
            dispatchers:
            [
                new StubDispatcher(new HashSet<string> { GraderKeys.Judge }),
                new StubDispatcher(new HashSet<string> { GraderKeys.CodeJudge, GraderKeys.IceScore })
            ],
            logger: NullLogger<CompositeAsyncGraderDispatcher>.Instance);

        var accepted = await composite.DispatchAsync(
            result: new QualityResult { RequestCorrelationId = "corr-1" },
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge, GraderKeys.CodeJudge, GraderKeys.IceScore },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<string> { GraderKeys.Judge, GraderKeys.CodeJudge, GraderKeys.IceScore }, actual: accepted);
    }

    [Fact]
    public async Task DispatchAsync_OneDispatcherThrows_OthersStillContributeTheirAcceptedKeys()
    {
        var composite = new CompositeAsyncGraderDispatcher(
            dispatchers: [new ThrowingDispatcher(), new StubDispatcher(new HashSet<string> { GraderKeys.Race })],
            logger: NullLogger<CompositeAsyncGraderDispatcher>.Instance);

        var accepted = await composite.DispatchAsync(
            result: new QualityResult { RequestCorrelationId = "corr-1" },
            pendingGraderKeys: new HashSet<string> { GraderKeys.Judge, GraderKeys.Race },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<string> { GraderKeys.Race }, actual: accepted);
    }

    private sealed class StubDispatcher(IReadOnlySet<string> accepts) : IAsyncGraderDispatcher
    {
        public Task<IReadOnlySet<string>> DispatchAsync(QualityResult result, IReadOnlySet<string> pendingGraderKeys,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult((IReadOnlySet<string>)new HashSet<string>(accepts.Intersect(pendingGraderKeys)));
        }
    }

    private sealed class ThrowingDispatcher : IAsyncGraderDispatcher
    {
        public Task<IReadOnlySet<string>> DispatchAsync(QualityResult result, IReadOnlySet<string> pendingGraderKeys,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("dispatcher is down");
        }
    }
}
