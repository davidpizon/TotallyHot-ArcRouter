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

    [Fact]
    public async Task GetAllAsync_RoundTripsEveryField()
    {
        var store = new SqliteJudgeShadowScoreStore(_database);
        var record = MakeRecord("corr-1");
        await store.InsertAsync(record: record, cancellationToken: TestContext.Current.CancellationToken);

        var rows = await store.GetAllAsync(TestContext.Current.CancellationToken);

        var round = Assert.Single(rows);
        Assert.Equal(expected: record.CorrelationId, actual: round.CorrelationId);
        Assert.Equal(expected: record.Dimension, actual: round.Dimension);
        Assert.Equal(expected: record.Model, actual: round.Model);
        Assert.Equal(expected: record.StaticScore, actual: round.StaticScore, precision: 9);
        Assert.Equal(expected: record.JudgeScore, actual: round.JudgeScore, precision: 9);
        Assert.Equal(expected: record.JudgeModel, actual: round.JudgeModel);
        Assert.Equal(expected: record.JudgePromptVersion, actual: round.JudgePromptVersion);
        Assert.Equal(expected: record.JudgeLatencyMs, actual: round.JudgeLatencyMs);
        Assert.Equal(expected: record.UsedLogprobs, actual: round.UsedLogprobs);
        Assert.Equal(expected: record.SyntaxAuthoritative, actual: round.SyntaxAuthoritative);
        Assert.Equal(expected: record.CreatedAtUtc, actual: round.CreatedAtUtc, precision: TimeSpan.FromSeconds(1));
        Assert.True(round.Id > 0, userMessage: "The store must return the assigned identity, not the zero it was handed.");
    }

    [Fact]
    public async Task GetAllAsync_ReturnsRowsOldestFirst()
    {
        var store = new SqliteJudgeShadowScoreStore(_database);
        await store.InsertAsync(record: MakeRecord("corr-1"), cancellationToken: TestContext.Current.CancellationToken);
        await store.InsertAsync(record: MakeRecord("corr-2"), cancellationToken: TestContext.Current.CancellationToken);
        await store.InsertAsync(record: MakeRecord("corr-3"), cancellationToken: TestContext.Current.CancellationToken);

        var rows = await store.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: ["corr-1", "corr-2", "corr-3"], actual: rows.Select(r => r.CorrelationId));
    }

    [Fact]
    public async Task GetAllAsync_EmptyTable_ReturnsEmptyRatherThanThrowing()
    {
        var store = new SqliteJudgeShadowScoreStore(_database);

        Assert.Empty(await store.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InsertAsync_RoundTripsSyntaxAuthoritativeBothWays(bool syntaxAuthoritative)
    {
        var store = new SqliteJudgeShadowScoreStore(_database);
        await store.InsertAsync(
            record: MakeRecord("corr-1") with { SyntaxAuthoritative = syntaxAuthoritative },
            cancellationToken: TestContext.Current.CancellationToken);

        var round = Assert.Single(await store.GetAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected: syntaxAuthoritative, actual: round.SyntaxAuthoritative);
    }

    [Fact]
    public async Task InsertAsync_NullSyntaxAuthoritative_StaysNullRatherThanCollapsingToFalse()
    {
        // The distinction the "unknown" cohort depends on. A null that read back as false would put every
        // provenance-less row into the heuristic bucket, which is a claim the data does not support.
        var store = new SqliteJudgeShadowScoreStore(_database);
        await store.InsertAsync(record: MakeRecord("corr-1") with { SyntaxAuthoritative = null },
            cancellationToken: TestContext.Current.CancellationToken);

        var round = Assert.Single(await store.GetAllAsync(TestContext.Current.CancellationToken));

        Assert.Null(round.SyntaxAuthoritative);
    }

    [Fact]
    public async Task EnsureCreated_OverADatabasePredatingTheColumn_AddsItAndLeavesHistoricalRowsNull()
    {
        // The Phase G2 migration. A row written before the column existed cannot have its authority
        // recovered - the language that decides it was never stored here - so it must read back as
        // unknown, not as one of the two real answers.
        using (var connection = _database.OpenConnection())
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE judge_shadow_scores DROP COLUMN syntax_authoritative;";
            drop.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO judge_shadow_scores (
                                     correlation_id, created_at_utc, dimension, model, static_score,
                                     judge_score, judge_model, judge_prompt_version, judge_latency_ms,
                                     used_logprobs)
                                 VALUES ('legacy', '2026-01-01T00:00:00.0000000+00:00', 'algorithm',
                                     'model-a', 0.5, 0.6, 'judge-a', 'g-eval-v1', 10, 1);
                                 """;
            insert.ExecuteNonQuery();
        }

        _database.EnsureCreated();

        var store = new SqliteJudgeShadowScoreStore(_database);
        var legacy = Assert.Single(await store.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Null(legacy.SyntaxAuthoritative);

        // And the migrated table still accepts a new row carrying a real verdict.
        await store.InsertAsync(record: MakeRecord("corr-new") with { SyntaxAuthoritative = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var rows = await store.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected: 2, actual: rows.Count);
        Assert.True(rows.Single(r => r.CorrelationId == "corr-new").SyntaxAuthoritative);
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
            true,
            SyntaxAuthoritative: true);
    }
}