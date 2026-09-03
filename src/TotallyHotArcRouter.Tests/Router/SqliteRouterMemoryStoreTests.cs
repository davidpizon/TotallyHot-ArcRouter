using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="SqliteRouterMemoryStore"/>'s aggregate persistence: a score must fold into the right
/// (dimension, model) row, survive a reload, and never be lost when observations race each other.
/// </summary>
public class SqliteRouterMemoryStoreTests : IDisposable
{
    private readonly RouterMemoryDatabase _database;
    private readonly string _tempDirectory;

    public SqliteRouterMemoryStoreTests()
    {
        _tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(path1: _tempDirectory, path2: "router_embedding_memory.db");
        _database = new RouterMemoryDatabase(
            Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        _database.EnsureCreated();
    }

    public void Dispose()
    {
        // ClearPool (scoped to this test's own connection string), not the process-global ClearAllPools:
        // under xUnit's parallel test execution, ClearAllPools can tear down a pooled native sqlite3
        // handle out from under a completely different test's in-flight query, surfacing as a spurious
        // ObjectDisposedException there. Matches SqliteMemoryEntryStoreTests' teardown.
        try
        {
            using var connection = _database.OpenConnection();
            SqliteConnection.ClearPool(connection);
        }
        catch (SqliteException)
        {
            // Best-effort cleanup; a database mid-teardown on a busy CI box is not a test failure.
        }

        try
        {
            if (Directory.Exists(_tempDirectory)) Directory.Delete(path: _tempDirectory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LoadAllAsync_EmptyDatabase_ReturnsEmptyMemory()
    {
        var store = new SqliteRouterMemoryStore(_database);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task RecordScoreAsync_FirstObservation_CreatesTheRow()
    {
        var store = new SqliteRouterMemoryStore(_database);

        await store.RecordScoreAsync(dimension: "live:bug_fix", model: "gpt-5.4", 0.8,
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);
        var aggregate = loaded["live:bug_fix"]["gpt-5.4"];
        Assert.Equal(0.8, actual: aggregate.Sum, 6);
        Assert.Equal(1, actual: aggregate.Count);
        Assert.Equal(0.8, actual: aggregate.Average!.Value, 6);
    }

    [Fact]
    public async Task RecordScoreAsync_RepeatedObservations_AccumulateIntoOneRow()
    {
        var store = new SqliteRouterMemoryStore(_database);

        await store.RecordScoreAsync(dimension: "live:bug_fix", model: "gpt-5.4", 0.8,
            cancellationToken: TestContext.Current.CancellationToken);
        await store.RecordScoreAsync(dimension: "live:bug_fix", model: "gpt-5.4", 1.0,
            cancellationToken: TestContext.Current.CancellationToken);
        await store.RecordScoreAsync(dimension: "live:bug_fix", model: "gpt-5.4", 0.6,
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);
        var models = Assert.Single(loaded).Value;
        var aggregate = Assert.Single(models).Value;
        Assert.Equal(3, actual: aggregate.Count);
        Assert.Equal(2.4, actual: aggregate.Sum, 6);
        Assert.Equal(0.8, actual: aggregate.Average!.Value, 6);
    }

    [Fact]
    public async Task RecordScoreAsync_KeepsDimensionsAndModelsIndependent()
    {
        var store = new SqliteRouterMemoryStore(_database);

        await store.RecordScoreAsync(dimension: "live:bug_fix", model: "gpt-5.4", 0.9,
            cancellationToken: TestContext.Current.CancellationToken);
        await store.RecordScoreAsync(dimension: "live:bug_fix", model: "kimi-k2.5", 0.2,
            cancellationToken: TestContext.Current.CancellationToken);
        await store.RecordScoreAsync(dimension: "live:algorithm", model: "gpt-5.4", 0.4,
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0.9, actual: loaded["live:bug_fix"]["gpt-5.4"].Average!.Value, 6);
        Assert.Equal(0.2, actual: loaded["live:bug_fix"]["kimi-k2.5"].Average!.Value, 6);
        Assert.Equal(0.4, actual: loaded["live:algorithm"]["gpt-5.4"].Average!.Value, 6);
    }

    [Fact]
    public async Task RecordScoreAsync_ConcurrentObservationsOfTheSamePair_LoseNothing()
    {
        // The reason RecordScoreAsync folds the score inside SQLite (ON CONFLICT DO UPDATE) instead of
        // reading the aggregate, adding in C#, and writing it back: a read-modify-write lets two racing
        // observations read the same starting value, and the later write discards the earlier score.
        var store = new SqliteRouterMemoryStore(_database);

        var tasks = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(
                function: () => store.RecordScoreAsync(dimension: "live:bug_fix", model: "gpt-5.4", 0.5,
                    cancellationToken: TestContext.Current.CancellationToken),
                cancellationToken: TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);
        var aggregate = loaded["live:bug_fix"]["gpt-5.4"];
        Assert.Equal(100, actual: aggregate.Count);
        Assert.Equal(50.0, actual: aggregate.Sum, 6);
    }

    [Fact]
    public async Task RecordScoreAsync_RejectsBlankKeys()
    {
        var store = new SqliteRouterMemoryStore(_database);

        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordScoreAsync(dimension: " ", model: "gpt-5.4", 0.5,
            cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordScoreAsync(dimension: "live:bug_fix", model: " ",
            0.5, cancellationToken: TestContext.Current.CancellationToken));
    }
}