using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GraderDrainService.ProcessAsync"/> for both the G-Eval judge (including the G2
/// persist-to-<c>judge_shadow_scores</c> hook) and a portfolio grader. The cached response text is read
/// without being removed; every give-up path abandons with its own reason.
/// </summary>
public class GraderDrainServiceTests
{
    [Fact]
    public async Task ProcessAsync_JudgeTextPresent_WritesExactlyOneShadowRowAndLeavesTheCache()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var client = new FakeClient(GraderKeys.Judge, 0.8, usedLogprobs: true, backboneModel: "free-judge-model");
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache: cache, clients: [client], shadowStore: store);

        await service.ProcessAsync(job: MakeJudgeJob("corr-1"), stoppingToken: TestContext.Current.CancellationToken);

        var record = Assert.Single(store.Inserted);
        Assert.Equal(expected: "corr-1", actual: record.CorrelationId);
        Assert.Equal(0.8, actual: record.JudgeScore);
        Assert.True(record.UsedLogprobs);
        Assert.Equal(expected: "free-judge-model", actual: record.JudgeModel);
        Assert.True(cache.TryPeek(correlationId: "corr-1", text: out _));
    }

    [Fact]
    public async Task ProcessAsync_JudgeTextPresent_CompletesTheQualityJoinWithTheJudgeScore()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: cache,
            clients: [new FakeClient(GraderKeys.Judge, 0.8, usedLogprobs: true, backboneModel: "free-judge-model")],
            aggregator: aggregator);

        await service.ProcessAsync(job: MakeJudgeJob("corr-1"), stoppingToken: TestContext.Current.CancellationToken);

        var completed = Assert.Single(aggregator.Completed);
        Assert.Equal(expected: "corr-1", actual: completed.CorrelationId);
        Assert.Equal(expected: GraderKeys.Judge, actual: completed.GraderKey);
        Assert.Equal(0.8, actual: completed.Score);
        Assert.True(completed.ViaJudgeSeam);
        Assert.Empty(aggregator.Abandoned);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessAsync_CarriesSyntaxAuthoritativeFromTheJobOntoThePersistedRow(bool syntaxAuthoritative)
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "public void M() { }");
        var store = new FakeJudgeShadowScoreStore();
        var service = CreateService(cache: cache,
            clients: [new FakeClient(GraderKeys.Judge, 0.8, usedLogprobs: true, backboneModel: "judge-a")],
            shadowStore: store);

        await service.ProcessAsync(
            job: MakeJudgeJob("corr-1") with { SyntaxAuthoritative = syntaxAuthoritative },
            stoppingToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: syntaxAuthoritative, actual: Assert.Single(store.Inserted).SyntaxAuthoritative);
    }

    [Fact]
    public async Task ProcessAsync_PromptCached_PassesItToTheJudgeRequest()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var promptCache = new PendingPromptCache(Options.Create(new JudgeOptions()));
        promptCache.Set(correlationId: "corr-1", prompt: "write a function that adds two numbers");
        var client = new FakeClient(GraderKeys.Judge, 0.8, usedLogprobs: true, backboneModel: "free-judge-model");
        var service = CreateService(cache: cache, clients: [client], promptCache: promptCache);

        await service.ProcessAsync(job: MakeJudgeJob("corr-1"), stoppingToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "write a function that adds two numbers", actual: client.LastRequest?.Prompt);
        Assert.True(promptCache.TryPeek(correlationId: "corr-1", prompt: out _));
    }

    [Theory]
    [InlineData(false, true, true, "judge-disabled")]
    [InlineData(true, false, true, "judge-text-evicted")]
    [InlineData(true, true, false, "judge-abstained")]
    public async Task ProcessAsync_JudgeGiveUpPath_AbandonsWithItsOwnReason(
        bool judgeEnabled, bool textCached, bool returnsScore, string expectedReason)
    {
        var cache = CreateCache();
        if (textCached) cache.Set(correlationId: "corr-1", text: "the agent's response");
        var aggregator = new RecordingAggregator();
        var service = CreateService(
            cache: cache,
            clients: [new FakeClient(GraderKeys.Judge, returnsScore ? 0.8 : null, usedLogprobs: true,
                backboneModel: "free-judge-model")],
            judgeOptions: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            { Enabled = judgeEnabled, PromptVersion = "g-eval-v1" }),
            aggregator: aggregator);

        await service.ProcessAsync(job: MakeJudgeJob("corr-1"), stoppingToken: TestContext.Current.CancellationToken);

        var abandoned = Assert.Single(aggregator.Abandoned);
        Assert.Equal(expected: GraderKeys.Judge, actual: abandoned.GraderKey);
        Assert.Equal(expected: expectedReason, actual: abandoned.Reason);
        Assert.Empty(aggregator.Completed);
    }

    [Fact]
    public async Task ProcessAsync_JudgeClientThrows_AbandonsAsFailed()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: cache,
            clients: [new FakeClient(GraderKeys.Judge, exception: new InvalidOperationException("backbone unreachable"))],
            aggregator: aggregator);

        await service.ProcessAsync(job: MakeJudgeJob("corr-1"), stoppingToken: TestContext.Current.CancellationToken);

        var abandoned = Assert.Single(aggregator.Abandoned);
        Assert.Equal(expected: "judge-failed", actual: abandoned.Reason);
        Assert.Empty(aggregator.Completed);
    }

    [Fact]
    public async Task ProcessAsync_PortfolioTextPresentAndEnabled_CompletesTheJoinAndLeavesTheCache()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: cache, clients: [new FakeClient(GraderKeys.CodeJudge, 0.8)],
            aggregator: aggregator);

        await service.ProcessAsync(job: MakePortfolioJob(GraderKeys.CodeJudge),
            stoppingToken: TestContext.Current.CancellationToken);

        var completed = Assert.Single(aggregator.Completed);
        Assert.Equal(expected: GraderKeys.CodeJudge, actual: completed.GraderKey);
        Assert.Equal(0.8, actual: completed.Score);
        Assert.False(completed.ViaJudgeSeam);
        Assert.Empty(aggregator.Abandoned);
        Assert.True(cache.TryPeek(correlationId: "corr-1", text: out _));
    }

    [Fact]
    public async Task ProcessAsync_PortfolioRecordsTheResolvedBackboneModel()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var backboneCache = new PendingGraderBackboneCache(Options.Create(new JudgeOptions()));
        var service = CreateService(cache: cache,
            clients: [new FakeClient(GraderKeys.CodeJudge, 0.8, backboneModel: "grader-backbone")],
            backboneCache: backboneCache);

        await service.ProcessAsync(job: MakePortfolioJob(GraderKeys.CodeJudge),
            stoppingToken: TestContext.Current.CancellationToken);

        Assert.True(backboneCache.TryTake(correlationId: "corr-1", backboneByGraderKey: out var recorded));
        Assert.Equal(expected: "grader-backbone", actual: recorded[GraderKeys.CodeJudge]);
    }

    [Fact]
    public async Task ProcessAsync_PortfolioDisabled_AbandonsWithDisabledReason()
    {
        var cache = CreateCache();
        cache.Set(correlationId: "corr-1", text: "the agent's response");
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: cache, clients: [new FakeClient(GraderKeys.CodeJudge, 0.8)],
            aggregator: aggregator,
            portfolioOptions: new StaticOptionsMonitor<PortfolioGraderOptions>(new PortfolioGraderOptions
            { CodeJudgeEnabled = false }));

        await service.ProcessAsync(job: MakePortfolioJob(GraderKeys.CodeJudge),
            stoppingToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "codejudge-disabled", actual: Assert.Single(aggregator.Abandoned).Reason);
    }

    [Fact]
    public async Task ProcessAsync_NoRegisteredClientForKey_AbandonsWithNotRegisteredReason()
    {
        var aggregator = new RecordingAggregator();
        var service = CreateService(cache: CreateCache(), clients: [], aggregator: aggregator);

        await service.ProcessAsync(job: MakePortfolioJob(GraderKeys.CodeJudge),
            stoppingToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "codejudge-not-registered", actual: Assert.Single(aggregator.Abandoned).Reason);
    }

    private static GraderScoringJob MakeJudgeJob(string correlationId)
    {
        return new GraderScoringJob(CorrelationId: correlationId, GraderKey: GraderKeys.Judge,
            Dimension: "algorithm", Model: "claude-opus-4-6", StaticScore: 0.6, SyntaxAuthoritative: true);
    }

    private static GraderScoringJob MakePortfolioJob(string graderKey, string correlationId = "corr-1")
    {
        return new GraderScoringJob(CorrelationId: correlationId, GraderKey: graderKey, Dimension: "bug_fixing",
            Model: "model-a", StaticScore: 0.5, SyntaxAuthoritative: false);
    }

    private static PendingResponseTextCache CreateCache()
    {
        return new PendingResponseTextCache(Options.Create(new JudgeOptions()));
    }

    private static GraderDrainService CreateService(
        PendingResponseTextCache cache,
        IEnumerable<IPortfolioGraderClient> clients,
        IJudgeShadowScoreStore? shadowStore = null,
        StaticOptionsMonitor<JudgeOptions>? judgeOptions = null,
        StaticOptionsMonitor<PortfolioGraderOptions>? portfolioOptions = null,
        IQualityScoreAggregator? aggregator = null,
        PendingPromptCache? promptCache = null,
        PendingGraderBackboneCache? backboneCache = null)
    {
        return new GraderDrainService(
            queue: new GraderQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 })),
            pendingResponseTextCache: cache,
            pendingPromptCache: promptCache ?? new PendingPromptCache(Options.Create(new JudgeOptions())),
            pendingGraderBackboneCache: backboneCache ?? new PendingGraderBackboneCache(Options.Create(new JudgeOptions())),
            clients: clients,
            shadowStore: shadowStore ?? new FakeJudgeShadowScoreStore(),
            judgeOptions: judgeOptions ?? new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            { Enabled = true, PromptVersion = "g-eval-v1" }),
            portfolioOptions: portfolioOptions ?? new StaticOptionsMonitor<PortfolioGraderOptions>(
                new PortfolioGraderOptions { CodeJudgeEnabled = true, IceScoreEnabled = true, RaceEnabled = true }),
            aggregator: aggregator ?? new RecordingAggregator(),
            logger: NullLogger<GraderDrainService>.Instance);
    }

    private sealed class FakeClient(
        string graderKey,
        double? score = null,
        Exception? exception = null,
        string backboneModel = "free-model",
        bool usedLogprobs = false) : IPortfolioGraderClient
    {
        public PortfolioGraderScoreRequest? LastRequest { get; private set; }

        public string GraderKey => graderKey;

        public Task<PortfolioGraderScoreResult?> ScoreAsync(PortfolioGraderScoreRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (exception is not null) return Task.FromException<PortfolioGraderScoreResult?>(exception);
            return Task.FromResult(score is { } value
                ? new PortfolioGraderScoreResult(Score: value, GraderModel: backboneModel, UsedLogprobs: usedLogprobs)
                : null);
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

        public Task<IReadOnlyList<JudgeShadowScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<JudgeShadowScoreRecord>>(Inserted);
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

    private sealed class RecordingAggregator : IQualityScoreAggregator
    {
        public List<(string CorrelationId, string GraderKey, double Score, bool ViaJudgeSeam)> Completed { get; } = [];

        public List<(string CorrelationId, string GraderKey, string Reason)> Abandoned { get; } = [];

        public Task SubmitAsync(QualityResult result, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> CompleteWithJudgeAsync(string correlationId, double judgeScore,
            CancellationToken cancellationToken = default)
        {
            Completed.Add((correlationId, GraderKeys.Judge, judgeScore, true));
            return Task.FromResult(true);
        }

        public Task<bool> AbandonJudgeAsync(string correlationId, string reason,
            CancellationToken cancellationToken = default)
        {
            Abandoned.Add((correlationId, GraderKeys.Judge, reason));
            return Task.FromResult(true);
        }

        public Task<bool> CompleteGraderAsync(string correlationId, string graderKey, double score,
            CancellationToken cancellationToken = default)
        {
            Completed.Add((correlationId, graderKey, score, false));
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
