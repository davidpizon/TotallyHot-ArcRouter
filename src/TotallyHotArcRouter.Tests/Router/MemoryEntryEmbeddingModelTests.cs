using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="MemoryEntry.MatchesEmbeddingModel"/>'s comparison rule and the
/// <c>embedding_model</c> column's round trip and additive migration through
/// <see cref="SqliteMemoryEntryStore"/>.
/// </summary>
public sealed class MemoryEntryEmbeddingModelTests
{
    /// <summary>An entry stamped with the current model's identity is comparable.</summary>
    [Fact]
    public void MatchesEmbeddingModel_SameIdentity_IsTrue() =>
        Assert.True(Entry("model-a").MatchesEmbeddingModel("model-a"));

    /// <summary>An entry from a different model is not comparable, whatever its vector length.</summary>
    [Fact]
    public void MatchesEmbeddingModel_DifferentIdentity_IsFalse() =>
        Assert.False(Entry("model-a").MatchesEmbeddingModel("model-b"));

    /// <summary>
    /// The deliberate optimistic reading of a pre-provenance row, documented at length on the method
    /// itself: treating null as a mismatch would silently discard an existing installation's entire
    /// corpus on the first startup after upgrading.
    /// </summary>
    [Fact]
    public void MatchesEmbeddingModel_NullIdentity_IsTreatedAsMatching() =>
        Assert.True(Entry(null).MatchesEmbeddingModel("model-a"));

    /// <summary>Identity comparison is ordinal - a case difference is a different model, not the same one.</summary>
    [Fact]
    public void MatchesEmbeddingModel_DifferingOnlyByCase_IsFalse() =>
        Assert.False(Entry("Model-A").MatchesEmbeddingModel("model-a"));

    /// <summary>The column round-trips through the store rather than being dropped on write or read.</summary>
    [Fact]
    public async Task AppendAndLoad_RoundTripsTheEmbeddingModel()
    {
        using var temp = new TempRouterMemoryDatabase();
        var store = new SqliteMemoryEntryStore(temp.Database);

        await store.AppendAsync(Entry("model-a"), TestContext.Current.CancellationToken);

        var loaded = Assert.Single(await store.LoadAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal("model-a", loaded.EmbeddingModel);
    }

    /// <summary>A null identity is stored as SQL NULL and read back as null, not as an empty string.</summary>
    [Fact]
    public async Task AppendAndLoad_NullEmbeddingModel_RoundTripsAsNull()
    {
        using var temp = new TempRouterMemoryDatabase();
        var store = new SqliteMemoryEntryStore(temp.Database);

        await store.AppendAsync(Entry(null), TestContext.Current.CancellationToken);

        var loaded = Assert.Single(await store.LoadAllAsync(TestContext.Current.CancellationToken));
        Assert.Null(loaded.EmbeddingModel);
    }

    /// <summary>
    /// The additive migration: a database created before the column existed gains it on the next
    /// <see cref="RouterMemoryDatabase.EnsureCreated"/>, and its pre-existing rows read back as null -
    /// unrecorded rather than falsely attributed to whatever model happens to be configured now.
    /// </summary>
    [Fact]
    public async Task EnsureCreated_DatabaseWithoutTheColumn_MigratesAndLeavesExistingRowsNull()
    {
        using var temp = new TempRouterMemoryDatabase();
        var store = new SqliteMemoryEntryStore(temp.Database);
        await store.AppendAsync(Entry("model-a"), TestContext.Current.CancellationToken);

        // Drop the column to simulate a database created before this provenance existed, then re-run the
        // schema/migration path exactly as startup would.
        using (var connection = temp.Database.OpenConnection())
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE memory_entries DROP COLUMN embedding_model;";
            drop.ExecuteNonQuery();
        }

        temp.Database.EnsureCreated();

        var loaded = Assert.Single(await store.LoadAllAsync(TestContext.Current.CancellationToken));
        Assert.Null(loaded.EmbeddingModel);
        Assert.True(loaded.MatchesEmbeddingModel("any-model"));
    }

    private static MemoryEntry Entry(string? embeddingModel) => new(
        Id: 0,
        TaskEmbedding: [1f, 0f],
        ChosenModel: "chosen-model",
        Score: 0.5,
        Cost: 0.0,
        VerifierTrace: null,
        CreatedAtUtc: DateTimeOffset.UtcNow,
        EmbeddingModel: embeddingModel);

    /// <summary>A <see cref="RouterMemoryDatabase"/> over a temp file, deleted on dispose.</summary>
    private sealed class TempRouterMemoryDatabase : IDisposable
    {
        private readonly string _directory;

        public TempRouterMemoryDatabase()
        {
            _directory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = Path.Combine(_directory, "router_memory.db"),
            }));
            Database.EnsureCreated();
        }

        public RouterMemoryDatabase Database { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A file still held by the SQLite pool on Windows is not worth failing a test over.
            }
        }
    }
}
