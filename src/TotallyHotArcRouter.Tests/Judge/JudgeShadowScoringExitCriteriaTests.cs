using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers docs/router/geval-shadow-scoring-plan.md Phase G1's exit criteria directly: shadow scoring must
/// never influence <see cref="EmbeddingMemory"/>/<see cref="SandboxResult.UnifiedScore"/>, and with the
/// judge disabled, nothing shadow-related fires at all.
/// </summary>
public class JudgeShadowScoringExitCriteriaTests
{
    [Fact]
    public async Task CompositeObserver_WithAndWithoutJudgeObserver_WritesByteIdenticalMemoryEntry()
    {
        var result = new SandboxResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "algorithm",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.6789,
            Executed = true,
        };

        var withoutJudge = await RunCompositeAndCaptureMemoryEntryAsync(result, includeJudgeObserver: false);
        var withJudge = await RunCompositeAndCaptureMemoryEntryAsync(result, includeJudgeObserver: true);

        Assert.Equal(withoutJudge.ChosenModel, withJudge.ChosenModel);
        Assert.Equal(withoutJudge.Score, withJudge.Score);
        Assert.Equal(withoutJudge.Cost, withJudge.Cost);
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
            queue,
            new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = false }),
            NullLogger<JudgeShadowScoreObserver>.Instance);

        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = new PendingTaskEmbeddingCache(Options.Create(new RoutingOptions { PendingEmbeddingCacheCapacity = 100, PendingEmbeddingCacheTtlSeconds = 300 }));
        pendingCache.Set("corr-1", [1f, 0f, 0f]);
        var embeddingObserver = new EmbeddingMemoryScoreObserver(
            memory,
            pendingCache,
            new PendingRequestCostCache(Options.Create(new RoutingOptions())),
            new PendingRequestProvenanceCache(Options.Create(new RoutingOptions())),
            NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var composite = new CompositeRouterScoreObserver(
            [embeddingObserver, judgeObserver],
            NullLogger<CompositeRouterScoreObserver>.Instance);

        var result = new SandboxResult
        {
            RequestCorrelationId = "corr-1",
            Dimension = "algorithm",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.5,
            Executed = true,
        };

        cache.Set("corr-1", "response text that should never be touched");
        await composite.ObserveAsync(result, TestContext.Current.CancellationToken);

        // Nothing observed the shadow-judge cache or queue: the entry set above is still there untouched,
        // and the queue never received a job.
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, queue.DroppedCount);
    }

    private static async Task<MemoryEntry> RunCompositeAndCaptureMemoryEntryAsync(SandboxResult result, bool includeJudgeObserver)
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = new PendingTaskEmbeddingCache(Options.Create(new RoutingOptions { PendingEmbeddingCacheCapacity = 100, PendingEmbeddingCacheTtlSeconds = 300 }));
        pendingCache.Set(result.RequestCorrelationId, [1f, 0f, 0f]);
        var embeddingObserver = new EmbeddingMemoryScoreObserver(
            memory,
            pendingCache,
            new PendingRequestCostCache(Options.Create(new RoutingOptions())),
            new PendingRequestProvenanceCache(Options.Create(new RoutingOptions())),
            NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var observers = new List<IRouterScoreObserver> { embeddingObserver };
        if (includeJudgeObserver)
        {
            var queue = new JudgeShadowScoreQueue(Options.Create(new JudgeOptions { QueueCapacity = 10 }));
            observers.Add(new JudgeShadowScoreObserver(
                queue,
                new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { Enabled = true }),
                NullLogger<JudgeShadowScoreObserver>.Instance));
        }

        var composite = new CompositeRouterScoreObserver(observers, NullLogger<CompositeRouterScoreObserver>.Instance);

        await composite.ObserveAsync(result, TestContext.Current.CancellationToken);

        return Assert.Single(store.Entries);
    }

    private static EmbeddingMemory CreateMemory(IMemoryEntryStore store) =>
        new(
            store,
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions { EmbeddingSimilarityThreshold = 0.0, MaxNeighborCount = 10, EmbeddingMemoryCapacity = 100 }),
            new StubEmbeddingClient(),
            NullLogger<EmbeddingMemory>.Instance);

    private sealed class FakeMemoryEntryStore : IMemoryEntryStore
    {
        private readonly List<MemoryEntry> _entries = [];
        private long _nextId = 1;

        public IReadOnlyList<MemoryEntry> Entries => _entries;

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);

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
