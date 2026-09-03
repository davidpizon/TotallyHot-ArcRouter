using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="EmbeddingMemory"/>'s embedding-model provenance: that entries are stamped with the
/// producing client's identity, and that <see cref="EmbeddingMemory.FindNearest"/> excludes entries which
/// are not comparable to the query rather than throwing partway through the scan.
/// </summary>
public sealed class EmbeddingMemoryModelIdentityTests
{
    /// <summary>
    /// The regression this suite exists for: a stored entry whose vector length differs from the query's
    /// used to reach <c>CosineSimilarity</c>, which throws on a length mismatch, aborting the whole
    /// retrieval. <c>OrchestratorRoutingPolicy</c> caught it and abstained, so nothing failed visibly -
    /// but <c>memory_kNN</c> then abstained through an error-logging exception path on every request until
    /// the stale entries aged out of a 20,000-entry FIFO.
    /// </summary>
    [Fact]
    public async Task FindNearest_EntryWithDifferentVectorLength_IsSkippedRatherThanThrowing()
    {
        var store = new InMemoryMemoryEntryStore();
        var memory = CreateMemory(store);

        // A pre-existing 3-dimensional entry, as if written before the embedding dimension changed.
        await store.AppendAsync(
            entry: new MemoryEntry(0, TaskEmbedding: [1f, 0f, 0f], ChosenModel: "old-model", 0.9, 0.0, null,
                CreatedAtUtc: DateTimeOffset.UtcNow),
            cancellationToken: TestContext.Current.CancellationToken);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var neighbors = memory.FindNearest([1f, 0f]);

        Assert.Empty(neighbors);
    }

    /// <summary>
    /// The silent half of the same problem: same vector length, different embedding model. No length check
    /// can see this, so without the identity comparison the two incomparable vectors would be scored
    /// against each other and returned as a confident neighbor.
    /// </summary>
    [Fact]
    public async Task FindNearest_EntryFromADifferentEmbeddingModel_IsSkipped()
    {
        var store = new InMemoryMemoryEntryStore();
        var memory = CreateMemory(store: store, modelIdentity: "model-b");

        await store.AppendAsync(entry: new MemoryEntry(
                0, TaskEmbedding: [1f, 0f], ChosenModel: "old-model", 0.9, 0.0, null,
                CreatedAtUtc: DateTimeOffset.UtcNow, EmbeddingModel: "model-a"),
            cancellationToken: TestContext.Current.CancellationToken);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var neighbors = memory.FindNearest([1f, 0f]);

        Assert.Empty(neighbors);
    }

    /// <summary>An entry from the same model is comparable and is returned as normal.</summary>
    [Fact]
    public async Task FindNearest_EntryFromTheSameEmbeddingModel_IsReturned()
    {
        var store = new InMemoryMemoryEntryStore();
        var memory = CreateMemory(store: store, modelIdentity: "model-a");

        await store.AppendAsync(entry: new MemoryEntry(
                0, TaskEmbedding: [1f, 0f], ChosenModel: "chosen-model", 0.9, 0.0, null,
                CreatedAtUtc: DateTimeOffset.UtcNow, EmbeddingModel: "model-a"),
            cancellationToken: TestContext.Current.CancellationToken);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var neighbors = memory.FindNearest([1f, 0f]);

        Assert.Equal(expected: "chosen-model", actual: Assert.Single(neighbors).Entry.ChosenModel);
    }

    /// <summary>
    /// A null <see cref="MemoryEntry.EmbeddingModel"/> is a row written before the column existed and is
    /// deliberately treated as comparable - see <see cref="MemoryEntry.MatchesEmbeddingModel"/>'s remarks
    /// for why the optimistic reading is correct rather than merely convenient. Reading it as a mismatch
    /// would discard an existing installation's whole corpus on the first startup after upgrading.
    /// </summary>
    [Fact]
    public async Task FindNearest_LegacyEntryWithNoRecordedModel_IsTreatedAsComparable()
    {
        var store = new InMemoryMemoryEntryStore();
        var memory = CreateMemory(store: store, modelIdentity: "model-a");

        await store.AppendAsync(entry: new MemoryEntry(
                0, TaskEmbedding: [1f, 0f], ChosenModel: "chosen-model", 0.9, 0.0, null,
                CreatedAtUtc: DateTimeOffset.UtcNow, EmbeddingModel: null),
            cancellationToken: TestContext.Current.CancellationToken);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var neighbors = memory.FindNearest([1f, 0f]);

        Assert.Equal(expected: "chosen-model", actual: Assert.Single(neighbors).Entry.ChosenModel);
    }

    /// <summary>A comparable entry is still returned even when an incomparable one sits ahead of it.</summary>
    [Fact]
    public async Task FindNearest_MixedComparableAndIncomparableEntries_ReturnsOnlyTheComparableOnes()
    {
        var store = new InMemoryMemoryEntryStore();
        var memory = CreateMemory(store: store, modelIdentity: "model-b");

        await store.AppendAsync(entry: new MemoryEntry(
                0, TaskEmbedding: [1f, 0f, 0f], ChosenModel: "wrong-length", 1.0, 0.0, null,
                CreatedAtUtc: DateTimeOffset.UtcNow, EmbeddingModel: "model-b"),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.AppendAsync(entry: new MemoryEntry(
                0, TaskEmbedding: [1f, 0f], ChosenModel: "wrong-model", 1.0, 0.0, null,
                CreatedAtUtc: DateTimeOffset.UtcNow, EmbeddingModel: "model-a"),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.AppendAsync(entry: new MemoryEntry(
                0, TaskEmbedding: [1f, 0f], ChosenModel: "keeper", 0.5, 0.0, null, CreatedAtUtc: DateTimeOffset.UtcNow,
                EmbeddingModel: "model-b"),
            cancellationToken: TestContext.Current.CancellationToken);
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var neighbors = memory.FindNearest([1f, 0f]);

        Assert.Equal(expected: "keeper", actual: Assert.Single(neighbors).Entry.ChosenModel);
    }

    /// <summary>Entries written through the memory carry the producing client's identity.</summary>
    [Fact]
    public async Task AddEntryAsync_StampsTheCurrentEmbeddingModelIdentity()
    {
        var store = new InMemoryMemoryEntryStore();
        var memory = CreateMemory(store: store, modelIdentity: "model-a");
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        var entry = await memory.AddEntryAsync(taskEmbedding: [1f, 0f], chosenModel: "chosen-model", 0.8, 0.0, null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "model-a", actual: entry.EmbeddingModel);
    }

    private static EmbeddingMemory CreateMemory(IMemoryEntryStore store, string? modelIdentity = null)
    {
        return new EmbeddingMemory(
            store: store,
            optionsMonitor: new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            {
                EmbeddingSimilarityThreshold = 0.1,
                MaxNeighborCount = 10,
                EmbeddingMemoryCapacity = 100
            }),
            embeddingClient: new StubEmbeddingClient(modelIdentity),
            logger: NullLogger<EmbeddingMemory>.Instance);
    }

    /// <summary>A minimal in-memory <see cref="IMemoryEntryStore"/> assigning increasing ids on append.</summary>
    private sealed class InMemoryMemoryEntryStore : IMemoryEntryStore
    {
        private readonly List<MemoryEntry> _entries = [];
        private long _nextId = 1;

        /// <inheritdoc/>
        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);
        }

        /// <inheritdoc/>
        public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            var persisted = entry with { Id = _nextId++ };
            _entries.Add(persisted);
            return Task.FromResult(persisted);
        }

        /// <inheritdoc/>
        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            _entries.RemoveAll(entry => entry.Id == id);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<long> GetMaxIdAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_entries.Count == 0 ? 0 : _entries.Max(entry => entry.Id));
        }
    }
}