using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="EmbeddingMemory"/>'s retrieval and eviction logic - PLAN.md Phase J's exit
/// criteria: kNN retrieval unit-tested for threshold, k, and FIFO eviction. Uses an in-memory fake
/// <see cref="IMemoryEntryStore"/> rather than SQLite so these stay well under the 5-second heavy-test
/// bound (AGENTS.md).
/// </summary>
public class EmbeddingMemoryTests
{
    [Fact]
    public async Task FindNearest_ReturnsOnlyEntriesMeetingSimilarityThreshold()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store, similarityThreshold: 0.9, maxNeighborCount: 10);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        await memory.AddEntryAsync(UnitVector(1, 0, 0), "model-exact", 0.8, 0.01, null, TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(UnitVector(0, 1, 0), "model-orthogonal", 0.8, 0.01, null, TestContext.Current.CancellationToken);

        var results = memory.FindNearest(UnitVector(1, 0, 0));

        var match = Assert.Single(results);
        Assert.Equal("model-exact", match.Entry.ChosenModel);
        Assert.Equal(1.0, match.Similarity, 3);
    }

    [Fact]
    public async Task FindNearest_LimitsResultsToMaxNeighborCount()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store, similarityThreshold: 0.0, maxNeighborCount: 2);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
        {
            await memory.AddEntryAsync(UnitVector(1, 0, 0), $"model-{i}", 0.5, 0.01, null, TestContext.Current.CancellationToken);
        }

        var results = memory.FindNearest(UnitVector(1, 0, 0));

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task FindNearest_OrdersBySimilarityDescending()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store, similarityThreshold: -1.0, maxNeighborCount: 10);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        // A slightly-off vector (lower cosine similarity) is added before the exact match, so ordering
        // can only come from the similarity sort, not insertion order.
        await memory.AddEntryAsync(UnitVector(1, 1, 0), "model-close", 0.5, 0.01, null, TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(UnitVector(1, 0, 0), "model-exact", 0.5, 0.01, null, TestContext.Current.CancellationToken);

        var results = memory.FindNearest(UnitVector(1, 0, 0));

        Assert.Equal("model-exact", results[0].Entry.ChosenModel);
        Assert.Equal("model-close", results[1].Entry.ChosenModel);
        Assert.True(results[0].Similarity > results[1].Similarity);
    }

    [Fact]
    public async Task AddEntryAsync_OverCapacity_EvictsOldestEntriesFirst()
    {
        var store = new FakeMemoryEntryStore();
        var memory = CreateMemory(store, similarityThreshold: -1.0, maxNeighborCount: 10, capacity: 3);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        await memory.AddEntryAsync(UnitVector(1, 0, 0), "model-oldest", 0.5, 0.01, null, TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(UnitVector(1, 0, 0), "model-second", 0.5, 0.01, null, TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(UnitVector(1, 0, 0), "model-third", 0.5, 0.01, null, TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(UnitVector(1, 0, 0), "model-newest", 0.5, 0.01, null, TestContext.Current.CancellationToken);

        var remainingModels = store.Entries.Select(e => e.ChosenModel).ToList();

        Assert.DoesNotContain("model-oldest", remainingModels);
        Assert.Equal(3, remainingModels.Count);
        Assert.Contains("model-newest", remainingModels);
    }

    [Fact]
    public async Task InitializeAsync_LoadsPriorEntriesFromStore()
    {
        var store = new FakeMemoryEntryStore();
        await store.AppendAsync(new MemoryEntry(0, UnitVector(1, 0, 0), "model-preexisting", 0.5, 0.01, null, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        var memory = CreateMemory(store, similarityThreshold: 0.9, maxNeighborCount: 10);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var results = memory.FindNearest(UnitVector(1, 0, 0));

        var match = Assert.Single(results);
        Assert.Equal("model-preexisting", match.Entry.ChosenModel);
    }

    [Fact]
    public async Task InitializeAsync_StoreOverCapacity_TrimsOldestAndDeletesFromStore()
    {
        var store = new FakeMemoryEntryStore();
        for (var i = 0; i < 5; i++)
        {
            await store.AppendAsync(new MemoryEntry(0, UnitVector(1, 0, 0), $"model-{i}", 0.5, 0.01, null, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        }

        var memory = CreateMemory(store, similarityThreshold: -1.0, maxNeighborCount: 10, capacity: 3);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var remainingModels = store.Entries.Select(e => e.ChosenModel).ToList();
        Assert.Equal(3, remainingModels.Count);
        Assert.DoesNotContain("model-0", remainingModels);
        Assert.DoesNotContain("model-1", remainingModels);
        Assert.Contains("model-4", remainingModels);

        var results = memory.FindNearest(UnitVector(1, 0, 0));
        Assert.Equal(3, results.Count);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T6: lowering
    /// <see cref="RoutingOptions.EmbeddingMemoryCapacity"/> at runtime (i.e. without a new
    /// <see cref="EmbeddingMemory"/> instance) must trim the working set and delete the evicted rows from
    /// the store, mirroring <see cref="AddEntryAsync_OverCapacity_EvictsOldestEntriesFirst"/> but driven by
    /// an <see cref="IOptionsMonitor{TOptions}.OnChange"/> notification instead of a new append.
    /// </summary>
    [Fact]
    public async Task OptionsMonitorChange_CapacityLowered_TrimsWorkingSetAndDeletesFromStore()
    {
        var store = new FakeMemoryEntryStore();
        var monitor = new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
        {
            EmbeddingSimilarityThreshold = -1.0,
            MaxNeighborCount = 10,
            EmbeddingMemoryCapacity = 20_000,
        });
        var memory = new EmbeddingMemory(store, monitor, new StubEmbeddingClient(), NullLogger<EmbeddingMemory>.Instance);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        await memory.AddEntryAsync(UnitVector(1, 0, 0), "model-oldest", 0.5, 0.01, null, TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(UnitVector(1, 0, 0), "model-second", 0.5, 0.01, null, TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(UnitVector(1, 0, 0), "model-newest", 0.5, 0.01, null, TestContext.Current.CancellationToken);

        monitor.Set(new RoutingOptions
        {
            EmbeddingSimilarityThreshold = -1.0,
            MaxNeighborCount = 10,
            EmbeddingMemoryCapacity = 1,
        });

        // The OnChange callback fires the trim without awaiting it (see EmbeddingMemory's remarks), so
        // await the same trim method directly here to observe its completion deterministically rather
        // than polling or sleeping for a background task.
        await memory.TrimToCurrentCapacityAsync(TestContext.Current.CancellationToken);

        var remainingModel = Assert.Single(store.Entries);
        Assert.Equal("model-newest", remainingModel.ChosenModel);
        Assert.Single(memory.FindNearest(UnitVector(1, 0, 0)));
    }

    private static EmbeddingMemory CreateMemory(
        IMemoryEntryStore store,
        double similarityThreshold,
        int maxNeighborCount,
        int capacity = 20_000,
        string? modelIdentity = null) =>
        new(
            store,
            new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            {
                EmbeddingSimilarityThreshold = similarityThreshold,
                MaxNeighborCount = maxNeighborCount,
                EmbeddingMemoryCapacity = capacity,
            }),
            new StubEmbeddingClient(modelIdentity),
            NullLogger<EmbeddingMemory>.Instance);

    private static float[] UnitVector(float x, float y, float z)
    {
        var length = MathF.Sqrt((x * x) + (y * y) + (z * z));
        return [x / length, y / length, z / length];
    }

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
