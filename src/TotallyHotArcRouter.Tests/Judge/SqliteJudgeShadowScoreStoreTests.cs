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
    private readonly string _tempDirectory;
    private readonly RouterMemoryDatabase _database;

    public SqliteJudgeShadowScoreStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(_tempDirectory, "router_embedding_memory.db");
        _database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = dbPath }));
        _database.EnsureCreated();
    }

    [Fact]
    public async Task InsertAsync_ThenGetRowCountAsync_ReflectsOneRow()
    {
        var store = new SqliteJudgeShadowScoreStore(_database);

        await store.InsertAsync(MakeRecord("corr-1"), TestContext.Current.CancellationToken);

        Assert.Equal(1, await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureCreated_CalledTwice_IsIdempotent()
    {
        _database.EnsureCreated();
        _database.EnsureCreated();

        var store = new SqliteJudgeShadowScoreStore(_database);
        await store.InsertAsync(MakeRecord("corr-1"), TestContext.Current.CancellationToken);

        Assert.Equal(1, await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteOldestAsync_RemovesOldestRowsFirst()
    {
        var store = new SqliteJudgeShadowScoreStore(_database);
        await store.InsertAsync(MakeRecord("corr-1"), TestContext.Current.CancellationToken);
        await store.InsertAsync(MakeRecord("corr-2"), TestContext.Current.CancellationToken);
        await store.InsertAsync(MakeRecord("corr-3"), TestContext.Current.CancellationToken);

        var deleted = await store.DeleteOldestAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        Assert.Equal(1, await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteBeforeAsync_RemovesOnlyRowsOlderThanCutoff()
    {
        var store = new SqliteJudgeShadowScoreStore(_database);
        var old = MakeRecord("corr-old") with { CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40) };
        var recent = MakeRecord("corr-recent") with { CreatedAtUtc = DateTimeOffset.UtcNow };
        await store.InsertAsync(old, TestContext.Current.CancellationToken);
        await store.InsertAsync(recent, TestContext.Current.CancellationToken);

        var deleted = await store.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Equal(1, await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    private static JudgeShadowScoreRecord MakeRecord(string correlationId) => new(
        Id: 0,
        CorrelationId: correlationId,
        CreatedAtUtc: DateTimeOffset.UtcNow,
        Dimension: "algorithm",
        Model: "claude-opus-4-6",
        StaticScore: 0.6,
        JudgeScore: 0.7,
        JudgeModel: "local-judge-model",
        JudgePromptVersion: "g-eval-v1",
        JudgeLatencyMs: 42,
        UsedLogprobs: true);

    public void Dispose()
    {
        try
        {
            using var connection = _database.OpenConnection();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Best-effort cleanup; a database mid-teardown on a busy CI box is not a test failure.
        }

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
