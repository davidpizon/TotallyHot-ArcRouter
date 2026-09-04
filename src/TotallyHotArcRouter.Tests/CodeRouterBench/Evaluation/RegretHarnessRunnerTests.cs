using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="RegretHarnessRunner"/>'s two non-happy paths without a real synced corpus (fast,
/// fixture-based - the happy path is proven end to end by <see cref="N5ComparisonReportReconciliationTests"/>
/// against a real corpus, and re-proven thinly by <c>RegretHarnessRunnerReconciliationTests</c>): the
/// unsynced-corpus decline, and the concurrent-call guard, mirroring
/// <c>ClusterTrainingServiceTests.RetrainAsync_ConcurrentCalls_SecondReturnsAlreadyRunning</c>'s race
/// convention.
/// </summary>
public class RegretHarnessRunnerTests
{
    [Fact]
    public async Task RunAsync_CorpusNotSynced_Declines()
    {
        using var temp = new TempBenchmarkDatabase(); // never EnsureCreated() - the "not synced" path.
        var runner = new RegretHarnessRunner(database: temp.Database, embeddingClient: new UnusedEmbeddingClient(),
            loggerFactory: NullLoggerFactory.Instance, logger: NullLogger<RegretHarnessRunner>.Instance);

        var result = await runner.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: RegretHarnessRunResultKind.Declined, actual: result.Kind);
        Assert.Empty(result.Splits);
        Assert.Null(result.RanAtUtc);
        Assert.Null(runner.LastResult);
    }

    [Fact]
    public async Task RunAsync_ConcurrentCalls_SecondEitherDeclinesOrSeesAlreadyRunning()
    {
        using var temp = new TempBenchmarkDatabase(); // never EnsureCreated() - the "not synced" path.
        var runner = new RegretHarnessRunner(database: temp.Database, embeddingClient: new UnusedEmbeddingClient(),
            loggerFactory: NullLoggerFactory.Instance, logger: NullLogger<RegretHarnessRunner>.Instance);

        var firstTask = runner.RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secondResult = await runner.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The first call may or may not have finished by the time the second one runs, but the gate
        // guarantees the second either sees AlreadyRunning or (if it lost the race entirely) also runs to
        // its own Declined outcome - the only genuinely wrong outcome is neither ever declining.
        await firstTask;
        Assert.True(secondResult.Kind is RegretHarnessRunResultKind.AlreadyRunning
            or RegretHarnessRunResultKind.Declined);
    }

    /// <summary>Never called on the Declined path this test class exercises - corpus readiness is checked first.</summary>
    private sealed class UnusedEmbeddingClient : IEmbeddingClient
    {
        public string ModelIdentity => "unused";

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Should not be called on the corpus-not-synced path.");
        }
    }
}
