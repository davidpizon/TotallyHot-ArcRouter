using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="EmbeddingMemoryScoreObserver"/> - docs/router/live-feedback-learning-plan.md Phase
/// 2c's exit criteria: a fake embedding client / pending cache writes exactly one memory_entries row for
/// a correlated result, and a cache miss is a logged no-op rather than an error.
/// </summary>
public class EmbeddingMemoryScoreObserverTests
{
    [Fact]
    public async Task ObserveAsync_PendingEmbeddingForCorrelationId_WritesExactlyOneEntry()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = CreatePendingCache();
        pendingCache.Set("corr-1", [1f, 0f, 0f]);

        var observer = new EmbeddingMemoryScoreObserver(memory, pendingCache, CreatePendingCostCache(), CreatePendingProvenanceCache(), NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var result = new SandboxResult
        {
            RequestCorrelationId = "corr-1",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.75,
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        var entry = Assert.Single(store.Entries);
        Assert.Equal("claude-opus-4-6", entry.ChosenModel);
        Assert.Equal(0.75, entry.Score);
    }

    [Fact]
    public async Task ObserveAsync_NoPendingEmbeddingForCorrelationId_WritesNoEntry()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = CreatePendingCache();
        var observer = new EmbeddingMemoryScoreObserver(memory, pendingCache, CreatePendingCostCache(), CreatePendingProvenanceCache(), NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var result = new SandboxResult
        {
            RequestCorrelationId = "never-set",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.75,
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task ObserveAsync_EmptyCorrelationId_WritesNoEntry()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = CreatePendingCache();
        var observer = new EmbeddingMemoryScoreObserver(memory, pendingCache, CreatePendingCostCache(), CreatePendingProvenanceCache(), NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var result = new SandboxResult { RequestCorrelationId = string.Empty, Model = "claude-opus-4-6", UnifiedScore = 0.5 };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task ObserveAsync_NoModelAttribution_WritesNoEntry()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = CreatePendingCache();
        pendingCache.Set("corr-1", [1f]);
        var observer = new EmbeddingMemoryScoreObserver(memory, pendingCache, CreatePendingCostCache(), CreatePendingProvenanceCache(), NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var result = new SandboxResult { RequestCorrelationId = "corr-1", Model = string.Empty, UnifiedScore = 0.5 };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task ObserveAsync_ClampsScoreIntoUnitInterval()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = CreatePendingCache();
        pendingCache.Set("corr-1", [1f]);
        var observer = new EmbeddingMemoryScoreObserver(memory, pendingCache, CreatePendingCostCache(), CreatePendingProvenanceCache(), NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var result = new SandboxResult { RequestCorrelationId = "corr-1", Model = "m", UnifiedScore = 5.0 };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, Assert.Single(store.Entries).Score);
    }

    [Fact]
    public async Task ObserveAsync_PendingCostAndProvenanceForCorrelationId_RecoversBothOntoTheEntry()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = CreatePendingCache();
        pendingCache.Set("corr-1", [1f, 0f, 0f]);
        var pendingCostCache = CreatePendingCostCache();
        pendingCostCache.Set("corr-1", 0.0042m);
        var pendingProvenanceCache = CreatePendingProvenanceCache();
        pendingProvenanceCache.Set("corr-1", isExploratory: true, propensity: 0.02);

        var observer = new EmbeddingMemoryScoreObserver(memory, pendingCache, pendingCostCache, pendingProvenanceCache, NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var result = new SandboxResult
        {
            RequestCorrelationId = "corr-1",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.75,
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        var entry = Assert.Single(store.Entries);
        Assert.Equal(0.0042, entry.Cost);
        Assert.True(entry.IsExploratory);
        Assert.Equal(0.02, entry.Propensity);
    }

    [Fact]
    public async Task ObserveAsync_NoPendingCostOrProvenance_DefaultsToZeroCostAndCertainNonExploratoryPropensity()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var pendingCache = CreatePendingCache();
        pendingCache.Set("corr-1", [1f, 0f, 0f]);

        var observer = new EmbeddingMemoryScoreObserver(memory, pendingCache, CreatePendingCostCache(), CreatePendingProvenanceCache(), NullLogger<EmbeddingMemoryScoreObserver>.Instance);

        var result = new SandboxResult
        {
            RequestCorrelationId = "corr-1",
            Model = "claude-opus-4-6",
            UnifiedScore = 0.75,
        };

        await observer.ObserveAsync(result, TestContext.Current.CancellationToken);

        var entry = Assert.Single(store.Entries);
        Assert.Equal(0.0, entry.Cost);
        Assert.False(entry.IsExploratory);
        Assert.Equal(1.0, entry.Propensity);
    }

    private static EmbeddingMemory CreateMemory(IMemoryEntryStore store) =>
        new(
            store,
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions { EmbeddingSimilarityThreshold = 0.0, MaxNeighborCount = 10, EmbeddingMemoryCapacity = 100 }),
            new StubEmbeddingClient(),
            NullLogger<EmbeddingMemory>.Instance);

    private static PendingTaskEmbeddingCache CreatePendingCache() =>
        new(Options.Create(new RoutingOptions { PendingEmbeddingCacheCapacity = 100, PendingEmbeddingCacheTtlSeconds = 300 }));

    private static PendingRequestCostCache CreatePendingCostCache() =>
        new(Options.Create(new RoutingOptions { PendingEmbeddingCacheCapacity = 100, PendingEmbeddingCacheTtlSeconds = 300 }));

    private static PendingRequestProvenanceCache CreatePendingProvenanceCache() =>
        new(Options.Create(new RoutingOptions { PendingEmbeddingCacheCapacity = 100, PendingEmbeddingCacheTtlSeconds = 300 }));

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
