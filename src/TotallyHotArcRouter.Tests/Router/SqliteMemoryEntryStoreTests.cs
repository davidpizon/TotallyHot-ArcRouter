using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="SqliteMemoryEntryStore"/>'s persistence round-trip: embedding vectors, scores, and
/// costs must survive a save/load cycle unchanged.
/// </summary>
public class SqliteMemoryEntryStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly RouterMemoryDatabase _database;

    public SqliteMemoryEntryStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(_tempDirectory, "router_embedding_memory.db");
        _database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        _database.EnsureCreated();
    }

    [Fact]
    public async Task AppendAsync_AssignsIncreasingIds()
    {
        var store = new SqliteMemoryEntryStore(_database);

        var first = await store.AppendAsync(MakeEntry([1, 0, 0], "model-a"), TestContext.Current.CancellationToken);
        var second = await store.AppendAsync(MakeEntry([0, 1, 0], "model-b"), TestContext.Current.CancellationToken);

        Assert.True(second.Id > first.Id);
    }

    [Fact]
    public async Task LoadAllAsync_RoundTripsEmbeddingScoreAndCost()
    {
        var store = new SqliteMemoryEntryStore(_database);
        var embedding = new float[] { 0.1f, -0.2f, 0.75f };
        await store.AppendAsync(new MemoryEntry(0, embedding, "model-c", 0.87, 0.0042, "trace-details", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(loaded);
        Assert.Equal(embedding, entry.TaskEmbedding);
        Assert.Equal("model-c", entry.ChosenModel);
        Assert.Equal(0.87, entry.Score, 6);
        Assert.Equal(0.0042, entry.Cost, 6);
        Assert.Equal("trace-details", entry.VerifierTrace);
    }

    [Fact]
    public async Task LoadAllAsync_OrdersByIdAscending()
    {
        var store = new SqliteMemoryEntryStore(_database);
        await store.AppendAsync(MakeEntry([1, 0, 0], "model-first"), TestContext.Current.CancellationToken);
        await store.AppendAsync(MakeEntry([0, 1, 0], "model-second"), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["model-first", "model-second"], loaded.Select(e => e.ChosenModel));
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyTheTargetedEntry()
    {
        var store = new SqliteMemoryEntryStore(_database);
        var toDelete = await store.AppendAsync(MakeEntry([1, 0, 0], "model-delete-me"), TestContext.Current.CancellationToken);
        await store.AppendAsync(MakeEntry([0, 1, 0], "model-keep"), TestContext.Current.CancellationToken);

        await store.DeleteAsync(toDelete.Id, TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);
        var remaining = Assert.Single(loaded);
        Assert.Equal("model-keep", remaining.ChosenModel);
    }

    [Fact]
    public async Task LoadAllAsync_NullVerifierTrace_RoundTripsAsNull()
    {
        var store = new SqliteMemoryEntryStore(_database);
        await store.AppendAsync(MakeEntry([1, 0, 0], "model-no-trace"), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(loaded).VerifierTrace);
    }

    private static MemoryEntry MakeEntry(float[] embedding, string model) =>
        new(0, embedding, model, 0.5, 0.01, null, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }
    }
}
