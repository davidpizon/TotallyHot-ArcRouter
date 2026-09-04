using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeShadowScoreDrainService.ProcessAsync"/> directly (mirroring
/// <see cref="TotallyHot.ArcRouter.Transcripts.TranscriptRetentionService.CheckAndPurgeAsync"/>'s
/// internal-for-test-access convention) - the exit criteria that matter most: the pending response text
/// is always consumed, and a scored job produces at most one shadow row.
/// </summary>
public class JudgeShadowScoreDrainServiceTests
{
    [Fact]
    public async Task ProcessAsync_TextPresent_WritesExactlyOneRowAndDrainsTheCache()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var judgeClient = new FakeJudgeClient(new JudgeScoreResult(0.8, true, JudgeModel: "free-judge-model"));
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache: cache, judgeClient: judgeClient, store: store);

        await service.ProcessAsync(job: MakeJob("corr-1"), stoppingToken: TestContext.Current.CancellationToken);

        var record = Assert.Single(store.Inserted);
        Assert.Equal(expected: "corr-1", actual: record.CorrelationId);
        Assert.Equal(0.8, actual: record.JudgeScore);
        Assert.True(record.UsedLogprobs);
        // The row names the model the client reported running, not a configured value.
        Assert.Equal(expected: "free-judge-model", actual: record.JudgeModel);
        Assert.False(cache.TryTake(correlationId: "corr-1", text: out _));
    }

    [Fact]
    public async Task ProcessAsync_NoPendingText_WritesNoRow()
    {
        var cache = CreateCache();
        var judgeClient = new FakeJudgeClient(new JudgeScoreResult(0.8, true, JudgeModel: "free-judge-model"));
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache: cache, judgeClient: judgeClient, store: store);

        await service.ProcessAsync(job: MakeJob("never-cached"), stoppingToken: TestContext.Current.CancellationToken);

        Assert.Empty(store.Inserted);
        Assert.False(judgeClient.WasCalled);
    }

    [Fact]
    public async Task ProcessAsync_JudgeClientThrows_TextIsStillDrainedAndNoRowWritten()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var judgeClient = new FakeJudgeClient(exception: new InvalidOperationException("backbone unreachable"));
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache: cache, judgeClient: judgeClient, store: store);

        await service.ProcessAsync(job: MakeJob("corr-1"), stoppingToken: TestContext.Current.CancellationToken);

        Assert.Empty(store.Inserted);
        Assert.False(cache.TryTake(correlationId: "corr-1", text: out _));
    }

    /// <summary>
    /// docs/router/geval-shadow-scoring-plan.md: a null score is an abstention - no free model is eligible -
    /// and must record nothing rather than a fabricated row, since the table exists to be compared against
    /// the Verifier.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_NoEligibleJudgeModel_WritesNoRowAndStillDrainsTheCache()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var judgeClient = new FakeJudgeClient(result: null);
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache: cache, judgeClient: judgeClient, store: store);

        await service.ProcessAsync(job: MakeJob("corr-1"), stoppingToken: TestContext.Current.CancellationToken);

        Assert.True(judgeClient.WasCalled);
        Assert.Empty(store.Inserted);
        Assert.False(cache.TryTake(correlationId: "corr-1", text: out _));
    }

    /// <summary>
    /// The judge toggle is live, so a job enqueued just before it was switched off must not still reach the
    /// backbone - and the retained response text must be released rather than left to age out.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_JudgeDisabled_NeverCallsBackboneButStillReleasesTheText()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var judgeClient = new FakeJudgeClient(new JudgeScoreResult(0.8, true, JudgeModel: "free-judge-model"));
        var store = new FakeJudgeShadowScoreStore();
        var options = new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = false });
        var service = CreateService(cache: cache, judgeClient: judgeClient, store: store, options: options);

        await service.ProcessAsync(job: MakeJob("corr-1"), stoppingToken: TestContext.Current.CancellationToken);

        Assert.False(judgeClient.WasCalled);
        Assert.Empty(store.Inserted);
        Assert.False(cache.TryTake(correlationId: "corr-1", text: out _));
    }

    private static PendingResponseTextCache CreateCache()
    {
        return new PendingResponseTextCache(Options.Create(new JudgeOptions()));
    }

    private static JudgeShadowScoreDrainService CreateService(
        PendingResponseTextCache cache,
        IJudgeClient judgeClient,
        IJudgeShadowScoreStore store,
        StaticOptionsMonitor<JudgeOptions>? options = null,
        IQualityScoreAggregator? aggregator = null)
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        return new JudgeShadowScoreDrainService(
            queue: queue,
            pendingResponseTextCache: cache,
            judgeClient: judgeClient,
            store: store,
            options: options ?? new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            { Enabled = true, PromptVersion = "g-eval-v1" }),
            aggregator: aggregator ?? new RecordingAggregator(),
            logger: NullLogger<JudgeShadowScoreDrainService>.Instance);
    }

    private static JudgeShadowScoringJob MakeJob(string correlationId)
    {
        return new JudgeShadowScoringJob(CorrelationId: correlationId, Dimension: "algorithm", Model: "claude-opus-4-6",
            0.6);
    }

    private sealed class FakeJudgeClient(JudgeScoreResult? result = null, Exception? exception = null) : IJudgeClient
    {
        private readonly Exception? _exception = exception;
        private readonly JudgeScoreResult? _result = result;

        public bool WasCalled { get; private set; }

        public Task<JudgeScoreResult?> ScoreAsync(JudgeScoreRequest request,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return _exception is not null
                ? Task.FromException<JudgeScoreResult?>(_exception)
                : Task.FromResult(_result);
        }
    }

    private sealed class FakeJudgeShadowScoreStore : IJudgeShadowScoreStore
    {
        public List<JudgeShadowScoreRecord> Inserted { get; } = [];

        public Task InsertAsync(JudgeShadowScoreRecord record, CancellationToken cancellationToken = default)
        {
            Inserted.Add(record);
            return Task.CompletedTask;
        }

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Inserted.Count);
        }

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }
    }

    /// <summary>
    /// Records what the drain worker did to the quality join, so the tests can assert the judge's grade
    /// actually reaches the aggregator - and that each failure path releases the held verdict instead of
    /// leaving it to time out.
    /// </summary>
    private sealed class RecordingAggregator : IQualityScoreAggregator
    {
        public List<(string CorrelationId, double Score)> Completed { get; } = [];

        public List<(string CorrelationId, string Reason)> Abandoned { get; } = [];

        public Task SubmitAsync(QualityResult result, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> CompleteWithJudgeAsync(string correlationId, double judgeScore,
            CancellationToken cancellationToken = default)
        {
            Completed.Add((correlationId, judgeScore));
            return Task.FromResult(true);
        }

        public Task<bool> AbandonJudgeAsync(string correlationId, string reason,
            CancellationToken cancellationToken = default)
        {
            Abandoned.Add((correlationId, reason));
            return Task.FromResult(true);
        }

        public Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }
    }
}