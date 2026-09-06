using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Quality.Scoring;
using TotallyHot.ArcRouter.Tests.Proxy;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// End-to-end coverage for docs/router/judge-join-deadlock-fix-plan.md: wires the real
/// <see cref="QualityScoreAggregator"/>, the real <see cref="JudgeAvailability"/>, the real
/// <see cref="JudgeShadowScoreDispatcher"/>/<see cref="JudgeShadowScoreQueue"/>, and
/// <see cref="JudgeShadowScoreDrainService.ProcessAsync"/> together - the exact combination that was never
/// exercised together before this fix, which is why the deadlock shipped undetected. Every test here never
/// advances a clock and never calls <see cref="QualityScoreAggregator.SweepExpiredAsync"/>: under the
/// pre-fix code, a held result reached the observer only after the join-timeout sweep ran, so a test that
/// never advances time is exactly the test that fails against the old trigger point.
/// </summary>
public class JudgeJoinDeadlockFixTests
{
    [Fact]
    public async Task HeldResult_JudgeDispatchedAtSubmit_ReachesObserverJudgedWithoutSweeping()
    {
        var responseTextCache = new PendingResponseTextCache(Options.Create(JudgeOptions()));
        responseTextCache.Set(correlationId: "corr-1", text: "the agent's response");

        var queue = new JudgeShadowScoreQueue(Options.Create(JudgeOptions()));
        var observer = new RecordingObserver();
        var aggregator = CreateAggregator(observer: observer, queue: queue, willJudge: true);

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        // Proves the dispatch actually reached the queue at hold-time, not merely that the aggregator held
        // the entry - the two used to be decoupled, which was the whole defect. Nothing has been written
        // to the observer yet: the entry is genuinely held, not degraded straight through.
        var job = await DequeueOneAsync(queue);
        Assert.NotNull(job);
        Assert.Empty(observer.Observed);

        var drainService = CreateDrainService(responseTextCache: responseTextCache, aggregator: aggregator,
            judgeResult: new JudgeScoreResult(0.8, UsedLogprobs: true, JudgeModel: "free-judge-model"));

        await drainService.ProcessAsync(job: job!, stoppingToken: TestContext.Current.CancellationToken);

        var written = Assert.Single(observer.Observed);
        Assert.Equal(0.8, actual: written.JudgeScore);
        Assert.Null(written.DegradedReason);
    }

    [Fact]
    public async Task HeldResult_QueueFull_WrittenImmediatelyAsNotDispatchedWithoutSweeping()
    {
        var queue = new JudgeShadowScoreQueue(Options.Create(JudgeOptions(queueCapacity: 1)));
        // Fill the single slot so the dispatcher's own enqueue attempt is the one that gets shed.
        Assert.True(queue.TryEnqueue(new JudgeShadowScoringJob(CorrelationId: "occupant", Dimension: "algorithm",
            Model: "model-a", StaticScore: 0.5)));

        var observer = new RecordingObserver();
        var aggregator = CreateAggregator(observer: observer, queue: queue, willJudge: true);

        await aggregator.SubmitAsync(result: Result(), cancellationToken: TestContext.Current.CancellationToken);

        var written = Assert.Single(observer.Observed);
        Assert.Null(written.JudgeScore);
        Assert.Equal(expected: "judge-not-dispatched", actual: written.DegradedReason);
    }

    private static QualityScoreAggregator CreateAggregator(IQualityScoreObserver observer,
        IJudgeShadowScoreQueue queue, bool willJudge)
    {
        var qualityOptions = new QualityOptions
        {
            JudgeJoinTimeoutMs = 60_000,
            JudgeJoinCapacity = 100,
            DimensionWeights = { ["algorithm"] = new DimensionWeightOptions { Syntax = 0.6, Analysis = 0.0, Judge = 0.4 } }
        };

        var judgeAvailability = willJudge
            ? new JudgeAvailability(options: EnabledJudgeMonitor(), modelSelector: CreateResolvingModelSelector())
            : (IJudgeAvailability)new NoJudgeAvailability();

        var dispatcher = new JudgeShadowScoreDispatcher(queue: queue, options: EnabledJudgeMonitor(),
            logger: NullLogger<JudgeShadowScoreDispatcher>.Instance);

        return new QualityScoreAggregator(
            observer: observer,
            scorer: new QualityScorer(Options.Create(qualityOptions)),
            judgeAvailability: judgeAvailability,
            asyncGraderDispatcher: dispatcher,
            options: Options.Create(qualityOptions),
            logger: NullLogger<QualityScoreAggregator>.Instance);
    }

    private static JudgeShadowScoreDrainService CreateDrainService(PendingResponseTextCache responseTextCache,
        IQualityScoreAggregator aggregator, JudgeScoreResult? judgeResult)
    {
        return new JudgeShadowScoreDrainService(
            queue: new JudgeShadowScoreQueue(Options.Create(JudgeOptions())),
            pendingResponseTextCache: responseTextCache,
            pendingPromptCache: new PendingPromptCache(Options.Create(JudgeOptions())),
            judgeClient: new FakeJudgeClient(result: judgeResult),
            store: new FakeJudgeShadowScoreStore(),
            options: EnabledJudgeMonitor(),
            aggregator: aggregator,
            logger: NullLogger<JudgeShadowScoreDrainService>.Instance);
    }

    private static JudgeModelSelector CreateResolvingModelSelector()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "free-judge-model",
            providerModelId: "free-judge-model",
            baseUrl: "http://localhost:1234/v1",
            isFree: true);

        return new JudgeModelSelector(routeResolver: resolver, options: EnabledJudgeMonitor(),
            logger: NullLogger<JudgeModelSelector>.Instance);
    }

    private static StaticOptionsMonitor<JudgeOptions> EnabledJudgeMonitor()
    {
        return new StaticOptionsMonitor<JudgeOptions>(JudgeOptions());
    }

    private static JudgeOptions JudgeOptions(int queueCapacity = 10)
    {
        return new JudgeOptions { Enabled = true, QueueCapacity = queueCapacity };
    }

    private static QualityResult Result(string correlationId = "corr-1")
    {
        return new QualityResult
        {
            RequestCorrelationId = correlationId,
            SessionId = "sess-1",
            Dimension = "algorithm",
            Model = "model-a",
            Language = nameof(CodeLanguage.CSharp),
            SyntaxValid = true,
            SyntaxAuthoritative = true,
            UnifiedScore = 1.0
        };
    }

    private static async Task<JudgeShadowScoringJob?> DequeueOneAsync(IJudgeShadowScoreQueue queue)
    {
        await foreach (var job in queue.DequeueAllAsync(TestContext.Current.CancellationToken)) return job;
        return null;
    }

    /// <summary>Records every result the aggregator writes, so "how many times" and "with what" are directly assertable.</summary>
    private sealed class RecordingObserver : IQualityScoreObserver
    {
        public List<QualityResult> Observed { get; } = [];

        public Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
        {
            Observed.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJudgeClient(JudgeScoreResult? result) : IJudgeClient
    {
        public Task<JudgeScoreResult?> ScoreAsync(JudgeScoreRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
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
}
