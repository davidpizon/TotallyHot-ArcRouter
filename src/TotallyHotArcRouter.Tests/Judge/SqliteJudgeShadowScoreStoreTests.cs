using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="SqliteJudgeShadowScoreStore"/> over a real (temp-file) <see cref="RouterMemoryDatabase"/>
/// - the shared-file additive migration docs/router/geval-shadow-scoring-plan.md §1d specifies.
/// </summary>
public class SqliteJudgeShadowScoreStoreTests : IDisposable
{
    private readonly RouterMemoryDatabase _database;
    private readonly string _tempDirectory;

    public SqliteJudgeShadowScoreStoreTests()
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
    public async Task InsertAsync_ThenGetRowCountAsync_ReflectsOneRow()
    {
        var store = new SqliteJudgeShadowScoreStore(_database);

        await store.InsertAsync(record: MakeRecord("corr-1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureCreated_CalledTwice_IsIdempotent()
    {
        _database.EnsureCreated();
        _database.EnsureCreated();

        var store = new SqliteJudgeShadowScoreStore(_database);
        await store.InsertAsync(record: MakeRecord("corr-1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteOldestAsync_RemovesOldestRowsFirst()
    {
        var store = new SqliteJudgeShadowScoreStore(_database);
        await store.InsertAsync(record: MakeRecord("corr-1"), cancellationToken: TestContext.Current.CancellationToken);
        await store.InsertAsync(record: MakeRecord("corr-2"), cancellationToken: TestContext.Current.CancellationToken);
        await store.InsertAsync(record: MakeRecord("corr-3"), cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await store.DeleteOldestAsync(2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, actual: deleted);
        Assert.Equal(1, actual: await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteBeforeAsync_RemovesOnlyRowsOlderThanCutoff()
    {
        var store = new SqliteJudgeShadowScoreStore(_database);
        var old = MakeRecord("corr-old") with { CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40) };
        var recent = MakeRecord("corr-recent") with { CreatedAtUtc = DateTimeOffset.UtcNow };
        await store.InsertAsync(record: old, cancellationToken: TestContext.Current.CancellationToken);
        await store.InsertAsync(record: recent, cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await store.DeleteBeforeAsync(cutoff: DateTimeOffset.UtcNow.AddDays(-30),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: deleted);
        Assert.Equal(1, actual: await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    private static JudgeShadowScoreRecord MakeRecord(string correlationId)
    {
        return new JudgeShadowScoreRecord(
            0,
            CorrelationId: correlationId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Dimension: "algorithm",
            Model: "claude-opus-4-6",
            0.6,
            0.7,
            JudgeModel: "local-judge-model",
            JudgePromptVersion: "g-eval-v1",
            42,
            true);
    }
}