using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="PortfolioGraderDrainService.ProcessAsync"/> directly, mirroring
/// <see cref="JudgeShadowScoreDrainServiceTests"/>'s convention: the cached response text is read without
/// being removed (so other concurrently-dispatched graders can still read it), a scored job completes the
/// aggregator's join for its own grader key, and every give-up path abandons with its own reason.
/// </summary>
public class PortfolioGraderDrainServiceTests
{
    [Fact]
    public async Task ProcessAsync_TextPresentAndEnabled_CompletesTheJoinAndLeavesTheCache()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var client = new FakeClient(GraderKeys.CodeJudge, 0.8);
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: cache, clients: [client], aggregator: aggregator);

        await service.ProcessAsync(job: MakeJob(GraderKeys.CodeJudge), stoppingToken: TestContext.Current.CancellationToken);

        var completed = Assert.Single(aggregator.Completed);
        Assert.Equal(expected: "corr-1", actual: completed.CorrelationId);
        Assert.Equal(expected: GraderKeys.CodeJudge, actual: completed.GraderKey);
        Assert.Equal(0.8, actual: completed.Score);
        Assert.Empty(aggregator.Abandoned);
        Assert.True(cache.TryPeek(correlationId: "corr-1", text: out _));
    }

    [Fact]
    public async Task ProcessAsync_GraderDisabled_AbandonsWithDisabledReason()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var client = new FakeClient(GraderKeys.CodeJudge, 0.8);
        var aggregator = new RecordingAggregator();
        var options = new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions { CodeJudgeEnabled = false });
        var service = CreateService(cache: cache, clients: [client], aggregator: aggregator, options: options);

        await service.ProcessAsync(job: MakeJob(GraderKeys.CodeJudge), stoppingToken: TestContext.Current.CancellationToken);

        var abandoned = Assert.Single(aggregator.Abandoned);
        Assert.Equal(expected: "codejudge-disabled", actual: abandoned.Reason);
        Assert.False(client.WasCalled);
    }

    [Fact]
    public async Task ProcessAsync_NoRegisteredClientForKey_AbandonsWithNotRegisteredReason()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: cache, clients: [], aggregator: aggregator);

        await service.ProcessAsync(job: MakeJob(GraderKeys.CodeJudge), stoppingToken: TestContext.Current.CancellationToken);

        var abandoned = Assert.Single(aggregator.Abandoned);
        Assert.Equal(expected: "codejudge-not-registered", actual: abandoned.Reason);
    }

    [Fact]
    public async Task ProcessAsync_NoPendingText_AbandonsWithTextEvictedReason()
    {
        var cache = CreateCache();
        var client = new FakeClient(GraderKeys.CodeJudge, 0.8);
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: cache, clients: [client], aggregator: aggregator);

        await service.ProcessAsync(job: MakeJob(GraderKeys.CodeJudge, correlationId: "never-cached"),
            stoppingToken: TestContext.Current.CancellationToken);

        var abandoned = Assert.Single(aggregator.Abandoned);
        Assert.Equal(expected: "codejudge-text-evicted", actual: abandoned.Reason);
        Assert.False(client.WasCalled);
    }

    [Fact]
    public async Task ProcessAsync_ClientAbstains_AbandonsWithAbstainedReason()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var client = new FakeClient(GraderKeys.CodeJudge, score: null);
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: cache, clients: [client], aggregator: aggregator);

        await service.ProcessAsync(job: MakeJob(GraderKeys.CodeJudge), stoppingToken: TestContext.Current.CancellationToken);

        var abandoned = Assert.Single(aggregator.Abandoned);
        Assert.Equal(expected: "codejudge-abstained", actual: abandoned.Reason);
        Assert.Empty(aggregator.Completed);
    }

    [Fact]
    public async Task ProcessAsync_ClientThrows_AbandonsWithFailedReason()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var client = new FakeClient(GraderKeys.CodeJudge, exception: new InvalidOperationException("backbone unreachable"));
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: cache, clients: [client], aggregator: aggregator);

        await service.ProcessAsync(job: MakeJob(GraderKeys.CodeJudge), stoppingToken: TestContext.Current.CancellationToken);

        var abandoned = Assert.Single(aggregator.Abandoned);
        Assert.Equal(expected: "codejudge-failed", actual: abandoned.Reason);
        Assert.Empty(aggregator.Completed);
    }

    private static PortfolioGraderJob MakeJob(string graderKey, string correlationId = "corr-1")
    {
        return new PortfolioGraderJob(CorrelationId: correlationId, GraderKey: graderKey, Dimension: "bug_fixing");
    }

    private static PendingResponseTextCache CreateCache()
    {
        return new PendingResponseTextCache(Options.Create(new JudgeOptions()));
    }

    private static PortfolioGraderDrainService CreateService(
        PendingResponseTextCache cache,
        IEnumerable<IPortfolioGraderClient> clients,
        IQualityScoreAggregator? aggregator = null,
        StaticOptionsMonitor<PortfolioGraderOptions>? options = null)
    {
        return new PortfolioGraderDrainService(
            queue: new PortfolioGraderQueue(Options.Create(new JudgeOptions())),
            pendingResponseTextCache: cache,
            pendingPromptCache: new PendingPromptCache(Options.Create(new JudgeOptions())),
            clients: clients,
            options: options ?? new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions
            { CodeJudgeEnabled = true, IceScoreEnabled = true, RaceEnabled = true }),
            aggregator: aggregator ?? new RecordingAggregator(),
            logger: NullLogger<PortfolioGraderDrainService>.Instance);
    }

    /// <summary>A controllable <see cref="IPortfolioGraderClient"/> double.</summary>
    private sealed class FakeClient(string graderKey, double? score = null, Exception? exception = null)
        : IPortfolioGraderClient
    {
        public bool WasCalled { get; private set; }

        public string GraderKey => graderKey;

        public Task<double?> ScoreAsync(PortfolioGraderScoreRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return exception is not null ? Task.FromException<double?>(exception) : Task.FromResult(score);
        }
    }

    /// <summary>Records what the drain worker did to the quality join.</summary>
    private sealed class RecordingAggregator : IQualityScoreAggregator
    {
        public List<(string CorrelationId, string GraderKey, double Score)> Completed { get; } = [];

        public List<(string CorrelationId, string GraderKey, string Reason)> Abandoned { get; } = [];

        public Task SubmitAsync(QualityResult result, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> CompleteWithJudgeAsync(string correlationId, double judgeScore,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> AbandonJudgeAsync(string correlationId, string reason,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> CompleteGraderAsync(string correlationId, string graderKey, double score,
            CancellationToken cancellationToken = default)
        {
            Completed.Add((correlationId, graderKey, score));
            return Task.FromResult(true);
        }

        public Task<bool> AbandonGraderAsync(string correlationId, string graderKey, string reason,
            CancellationToken cancellationToken = default)
        {
            Abandoned.Add((correlationId, graderKey, reason));
            return Task.FromResult(true);
        }

        public Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }
    }
}
