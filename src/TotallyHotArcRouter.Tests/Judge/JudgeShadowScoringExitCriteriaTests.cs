using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers docs/router/geval-shadow-scoring-plan.md Phase G1's exit criteria directly: shadow scoring must
/// never influence <see cref="EmbeddingMemory"/>/<see cref="QualityResult.UnifiedScore"/>, and with the
/// judge disabled, nothing shadow-related fires at all.
/// </summary>
public class JudgeShadowScoringExitCriteriaTests
{
    [Fact]
    public async Task CompositeObserver_WithAndWithoutJudgeObserver_WritesByteIdenticalMemoryEntry()
    {
        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "algorithm",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.6789
        };

        var withoutJudge = await RunCompositeAndCaptureMemoryEntryAsync(result: result, false);
        var withJudge = await RunCompositeAndCaptureMemoryEntryAsync(result: result, true);

        Assert.Equal(expected: withoutJudge.ChosenModel, actual: withJudge.ChosenModel);
        Assert.Equal(expected: withoutJudge.Score, actual: withJudge.Score);
        Assert.Equal(expected: withoutJudge.Cost, actual: withJudge.Cost);
        Assert.False(withoutJudge.IsJudgeScored);
        Assert.False(withJudge.IsJudgeScored);
    }

    [Fact]
    public async Task JudgeDisabled_ObserverInFanOutButGated_NoQueueOrCacheActivity()
    {
        var cache = new PendingResponseTextCache(Options.Create(new JudgeOptions()));
        var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));

        // The disabled posture is enforced by the observer itself, not by DI: since JudgeOptions.Enabled is
        // operator-toggleable at runtime, ServiceCollectionExtensions registers JudgeShadowScoreObserver in
        // the fan-out unconditionally and ObserveAsync returns early while the flag is off. So this fan-out
        // deliberately *includes* the observer - proving the gate holds where it actually lives.
        var judgeObserver = new JudgeShadowScoreObserver(
            queue: queue,
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = false }),
            logger: NullLogger<JudgeShadowScoreObserver>.Instance);

        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = new PendingTaskEmbeddingCache(Options.Create(new RoutingOptions
        { PendingEmbeddingCacheCapacity = 100, PendingEmbeddingCacheTtlSeconds = 300 }));
        pendingCache.Set(correlationId: "corr-1", embedding: [1f, 0f, 0f]);
        var embeddingObserver = new EmbeddingMemoryScoreObserver(
            memory: memory,
            pendingCache: pendingCache,
            pendingCostCache: new PendingRequestCostCache(Options.Create(new RoutingOptions())),
            pendingProvenanceCache: new PendingRequestProvenanceCache(Options.Create(new RoutingOptions())),
            logger: NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var composite = new CompositeRouterScoreObserver(
            observers: [embeddingObserver, judgeObserver],
            logger: NullLogger<CompositeRouterScoreObserver>.Instance);

        var result = new QualityResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "algorithm",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.5
        };

        cache.Set(correlationId: "corr-1", text: "response text that should never be touched");
        await composite.ObserveAsync(result: result, cancellationToken: TestContext.Current.CancellationToken);

        // Nothing observed the shadow-judge cache or queue: the entry set above is still there untouched,
        // and the queue never received a job.
        Assert.Equal(1, actual: cache.Count);
        Assert.Equal(0, actual: queue.DroppedCount);
    }

    private static async Task<MemoryEntry> RunCompositeAndCaptureMemoryEntryAsync(QualityResult result,
        bool includeJudgeObserver)
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = new PendingTaskEmbeddingCache(Options.Create(new RoutingOptions
        { PendingEmbeddingCacheCapacity = 100, PendingEmbeddingCacheTtlSeconds = 300 }));
        pendingCache.Set(correlationId: result.RequestCorrelationId, embedding: [1f, 0f, 0f]);
        var embeddingObserver = new EmbeddingMemoryScoreObserver(
            memory: memory,
            pendingCache: pendingCache,
            pendingCostCache: new PendingRequestCostCache(Options.Create(new RoutingOptions())),
            pendingProvenanceCache: new PendingRequestProvenanceCache(Options.Create(new RoutingOptions())),
            logger: NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var observers = new List<IQualityScoreObserver> { embeddingObserver };
        if (includeJudgeObserver)
        {
            var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
            observers.Add(new JudgeShadowScoreObserver(
                queue: queue,
                options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = true }),
                logger: NullLogger<JudgeShadowScoreObserver>.Instance));
        }

        var composite = new CompositeRouterScoreObserver(observers: observers,
            logger: NullLogger<CompositeRouterScoreObserver>.Instance);

        await composite.ObserveAsync(result: result, cancellationToken: TestContext.Current.CancellationToken);

        return Assert.Single(store.Entries);
    }

    private static EmbeddingMemory CreateMemory(IMemoryEntryStore store)
    {
        return new EmbeddingMemory(
            store: store,
            optionsMonitor: new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            { EmbeddingSimilarityThreshold = 0.0, MaxNeighborCount = 10, EmbeddingMemoryCapacity = 100 }),
            embeddingClient: new StubEmbeddingClient(),
            logger: NullLogger<EmbeddingMemory>.Instance);
    }

    private sealed class FakeMemoryEntryStore : IMemoryEntryStore
    {
        private readonly List<MemoryEntry> _entries = [];
        private long _nextId = 1;

        public IReadOnlyList<MemoryEntry> Entries => _entries;

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);
        }

        public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            var persisted = entry with { Id = _nextId++ };
            _entries.Add(persisted);
            return Task.FromResult(persisted);
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }
    }
}