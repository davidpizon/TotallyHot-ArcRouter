using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="SqliteMemoryEntryStore"/>'s persistence round-trip: embedding vectors, scores, and
/// costs must survive a save/load cycle unchanged.
/// </summary>
public class SqliteMemoryEntryStoreTests : IDisposable
{
    private readonly RouterMemoryDatabase _database;
    private readonly string _tempDirectory;

    public SqliteMemoryEntryStoreTests()
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
        // ObjectDisposedException there.
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
    }

    [Fact]
    public async Task AppendAsync_AssignsIncreasingIds()
    {
        var store = new SqliteMemoryEntryStore(_database);

        var first = await store.AppendAsync(entry: MakeEntry(embedding: [1, 0, 0], model: "model-a"),
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await store.AppendAsync(entry: MakeEntry(embedding: [0, 1, 0], model: "model-b"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(second.Id > first.Id);
    }

    [Fact]
    public async Task LoadAllAsync_RoundTripsEmbeddingScoreAndCost()
    {
        var store = new SqliteMemoryEntryStore(_database);
        var embedding = new[] { 0.1f, -0.2f, 0.75f };
        await store.AppendAsync(
            entry: new MemoryEntry(0, TaskEmbedding: embedding, ChosenModel: "model-c", 0.87, 0.0042,
                VerifierTrace: "trace-details", CreatedAtUtc: DateTimeOffset.UtcNow),
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(loaded);
        Assert.Equal(expected: embedding, actual: entry.TaskEmbedding);
        Assert.Equal(expected: "model-c", actual: entry.ChosenModel);
        Assert.Equal(0.87, actual: entry.Score, 6);
        Assert.Equal(0.0042, actual: entry.Cost, 6);
        Assert.Equal(expected: "trace-details", actual: entry.VerifierTrace);
        Assert.False(entry.IsExploratory);
        Assert.Equal(1.0, actual: entry.Propensity, 6);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T1c: is_exploratory/propensity round-trip
    /// through the store like every other column.
    /// </summary>
    [Fact]
    public async Task LoadAllAsync_RoundTripsProvenance()
    {
        var store = new SqliteMemoryEntryStore(_database);
        var embedding = new[] { 0.1f, -0.2f, 0.75f };
        await store.AppendAsync(
            entry: new MemoryEntry(0, TaskEmbedding: embedding, ChosenModel: "model-c", 0.87, 0.0042, null,
                CreatedAtUtc: DateTimeOffset.UtcNow, true, 0.0167),
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(loaded);
        Assert.True(entry.IsExploratory);
        Assert.Equal(0.0167, actual: entry.Propensity, 6);
    }

    /// <summary>
    /// docs/router/self-organizing-classification-plan.md Phase T1c: a database created before provenance
    /// tracking existed (schema without is_exploratory/propensity) picks up the columns on the next
    /// EnsureCreated call, with existing rows defaulting to non-exploratory, certain propensity.
    /// </summary>
    [Fact]
    public async Task EnsureCreated_PreExistingDatabaseWithoutProvenanceColumns_MigratesWithDefaults()
    {
        await using var connection = _database.OpenConnection();
        using (var drop = connection.CreateCommand())
        {
            // Simulate a pre-migration database: recreate memory_entries without the two new columns,
            // then seed one row exactly as a pre-existing install would have it.
            drop.CommandText = """
                               DROP TABLE memory_entries;
                               CREATE TABLE memory_entries (
                                   id               INTEGER PRIMARY KEY AUTOINCREMENT,
                                   embedding        BLOB    NOT NULL,
                                   chosen_model     TEXT    NOT NULL,
                                   score            REAL    NOT NULL,
                                   cost             REAL    NOT NULL,
                                   verifier_trace   TEXT    NULL,
                                   created_at_utc   TEXT    NOT NULL
                               );
                               INSERT INTO memory_entries (embedding, chosen_model, score, cost, verifier_trace, created_at_utc)
                               VALUES (x'0000803F', 'legacy-model', 0.5, 0.01, NULL, '2026-01-01T00:00:00.0000000+00:00');
                               """;
            drop.ExecuteNonQuery();
        }

        _database.EnsureCreated();

        var store = new SqliteMemoryEntryStore(_database);
        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(loaded);
        Assert.Equal(expected: "legacy-model", actual: entry.ChosenModel);
        Assert.False(entry.IsExploratory);
        Assert.Equal(1.0, actual: entry.Propensity, 6);
    }

    /// <summary>
    /// docs/router/geval-shadow-scoring-plan.md §Provenance: is_judge_scored round-trips through the store
    /// like every other column, and defaults to false for a caller that doesn't set it.
    /// </summary>
    [Fact]
    public async Task LoadAllAsync_RoundTripsIsJudgeScored()
    {
        var store = new SqliteMemoryEntryStore(_database);
        var embedding = new[] { 0.1f, -0.2f, 0.75f };
        await store.AppendAsync(
            entry: new MemoryEntry(0, TaskEmbedding: embedding, ChosenModel: "model-c", 0.87, 0.0042, null,
                CreatedAtUtc: DateTimeOffset.UtcNow, IsJudgeScored: true),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.AppendAsync(entry: MakeEntry(embedding: [1, 0, 0], model: "model-default"),
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        Assert.True(loaded.Single(e => e.ChosenModel == "model-c").IsJudgeScored);
        Assert.False(loaded.Single(e => e.ChosenModel == "model-default").IsJudgeScored);
    }

    /// <summary>
    /// docs/router/geval-shadow-scoring-plan.md Phase G1e: a database created before the is_judge_scored
    /// column existed picks it up on the next EnsureCreated call, with existing rows defaulting to false.
    /// </summary>
    [Fact]
    public async Task EnsureCreated_PreExistingDatabaseWithoutIsJudgeScoredColumn_MigratesWithDefault()
    {
        using (var connection = _database.OpenConnection())
        using (var alter = connection.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE memory_entries DROP COLUMN is_judge_scored;";
            alter.ExecuteNonQuery();
        }

        _database.EnsureCreated();
        _database.EnsureCreated(); // Idempotency: a second call must not throw or duplicate the column.

        var store = new SqliteMemoryEntryStore(_database);
        await store.AppendAsync(entry: MakeEntry(embedding: [1, 0, 0], model: "model-post-migration"),
            cancellationToken: TestContext.Current.CancellationToken);
        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(loaded).IsJudgeScored);
    }

    [Fact]
    public async Task LoadAllAsync_OrdersByIdAscending()
    {
        var store = new SqliteMemoryEntryStore(_database);
        await store.AppendAsync(entry: MakeEntry(embedding: [1, 0, 0], model: "model-first"),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.AppendAsync(entry: MakeEntry(embedding: [0, 1, 0], model: "model-second"),
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: ["model-first", "model-second"], actual: loaded.Select(e => e.ChosenModel));
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyTheTargetedEntry()
    {
        var store = new SqliteMemoryEntryStore(_database);
        var toDelete = await store.AppendAsync(entry: MakeEntry(embedding: [1, 0, 0], model: "model-delete-me"),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.AppendAsync(entry: MakeEntry(embedding: [0, 1, 0], model: "model-keep"),
            cancellationToken: TestContext.Current.CancellationToken);

        await store.DeleteAsync(id: toDelete.Id, cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);
        var remaining = Assert.Single(loaded);
        Assert.Equal(expected: "model-keep", actual: remaining.ChosenModel);
    }

    [Fact]
    public async Task LoadAllAsync_NullVerifierTrace_RoundTripsAsNull()
    {
        var store = new SqliteMemoryEntryStore(_database);
        await store.AppendAsync(entry: MakeEntry(embedding: [1, 0, 0], model: "model-no-trace"),
            cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await store.LoadAllAsync(TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(loaded).VerifierTrace);
    }

    private static MemoryEntry MakeEntry(float[] embedding, string model)
    {
        return new MemoryEntry(0, TaskEmbedding: embedding, ChosenModel: model, 0.5, 0.01, null,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }
}