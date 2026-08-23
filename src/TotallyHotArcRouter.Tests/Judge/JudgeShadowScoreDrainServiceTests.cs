using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        cache.Set("corr-1", "the agent's response");
        var judgeClient = new FakeJudgeClient(new JudgeScoreResult(0.8, UsedLogprobs: true, "free-judge-model"));
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache, judgeClient, store);

        await service.ProcessAsync(MakeJob("corr-1"), TestContext.Current.CancellationToken);

        var record = Assert.Single(store.Inserted);
        Assert.Equal("corr-1", record.CorrelationId);
        Assert.Equal(0.8, record.JudgeScore);
        Assert.True(record.UsedLogprobs);
        // The row names the model the client reported running, not a configured value.
        Assert.Equal("free-judge-model", record.JudgeModel);
        Assert.False(cache.TryTake("corr-1", out _));
    }

    [Fact]
    public async Task ProcessAsync_NoPendingText_WritesNoRow()
    {
        var cache = CreateCache();
        var judgeClient = new FakeJudgeClient(new JudgeScoreResult(0.8, UsedLogprobs: true, "free-judge-model"));
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache, judgeClient, store);

        await service.ProcessAsync(MakeJob("never-cached"), TestContext.Current.CancellationToken);

        Assert.Empty(store.Inserted);
        Assert.False(judgeClient.WasCalled);
    }

    [Fact]
    public async Task ProcessAsync_JudgeClientThrows_TextIsStillDrainedAndNoRowWritten()
    {
        var cache = CreateCache();
        cache.Set("corr-1", "the agent's response");
        var judgeClient = new FakeJudgeClient(exception: new InvalidOperationException("backbone unreachable"));
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache, judgeClient, store);

        await service.ProcessAsync(MakeJob("corr-1"), TestContext.Current.CancellationToken);

        Assert.Empty(store.Inserted);
        Assert.False(cache.TryTake("corr-1", out _));
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
        cache.Set("corr-1", "the agent's response");
        var judgeClient = new FakeJudgeClient(result: null);
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache, judgeClient, store);

        await service.ProcessAsync(MakeJob("corr-1"), TestContext.Current.CancellationToken);

        Assert.True(judgeClient.WasCalled);
        Assert.Empty(store.Inserted);
        Assert.False(cache.TryTake("corr-1", out _));
    }

    /// <summary>
    /// The judge toggle is live, so a job enqueued just before it was switched off must not still reach the
    /// backbone - and the retained response text must be released rather than left to age out.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_JudgeDisabled_NeverCallsBackboneButStillReleasesTheText()
    {
        var cache = CreateCache();
        cache.Set("corr-1", "the agent's response");
        var judgeClient = new FakeJudgeClient(new JudgeScoreResult(0.8, UsedLogprobs: true, "free-judge-model"));
        var store = new FakeJudgeShadowScoreStore();
        var options = new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = false });
        var service = CreateService(cache, judgeClient, store, options);

        await service.ProcessAsync(MakeJob("corr-1"), TestContext.Current.CancellationToken);

        Assert.False(judgeClient.WasCalled);
        Assert.Empty(store.Inserted);
        Assert.False(cache.TryTake("corr-1", out _));
    }

    private static PendingResponseTextCache CreateCache() =>
        new(Options.Create(new JudgeOptions()));

    private static JudgeShadowScoreDrainService CreateService(
        PendingResponseTextCache cache,
        IJudgeClient judgeClient,
        IJudgeShadowScoreStore store,
        StaticOptionsMonitor<JudgeOptions>? options = null)
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
        return new JudgeShadowScoreDrainService(
            queue,
            cache,
            judgeClient,
            store,
            options ?? new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = true, PromptVersion = "g-eval-v1" }),
            NullLogger<JudgeShadowScoreDrainService>.Instance);
    }

    private static JudgeShadowScoringJob MakeJob(string correlationId) =>
        new(correlationId, "algorithm", "claude-opus-4-6", VerifierScore: 0.6, Executed: true);

    private sealed class FakeJudgeClient : IJudgeClient
    {
        private readonly JudgeScoreResult? _result;
        private readonly Exception? _exception;

        public bool WasCalled { get; private set; }

        public FakeJudgeClient(JudgeScoreResult? result = null, Exception? exception = null)
        {
            _result = result;
            _exception = exception;
        }

        public Task<JudgeScoreResult?> ScoreAsync(JudgeScoreRequest request, CancellationToken cancellationToken = default)
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

        public Task<int> GetRowCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Inserted.Count);

        public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
