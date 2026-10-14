using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>Covers <c>grade_receipts</c> persistence, migration, and HMAC storage (issue #293 phase 4).</summary>
public class GradeReceiptStoreTests
{
    [Fact]
    public void EnsureCreated_AddsGradeReceiptsAndGraderScoreColumns()
    {
        var path = CreateLegacyDatabaseWithoutIssue293Tables();
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = path }));

        database.EnsureCreated();

        using var connection = database.OpenConnection();
        Assert.True(ColumnExists(connection: connection, table: "grade_receipts", column: "question_hmac"));
        Assert.True(ColumnExists(connection: connection, table: "grader_scores", column: "question_hmac"));
        Assert.True(ColumnExists(connection: connection, table: "grader_scores", column: "prompt_version"));
    }

    [Fact]
    public async Task GradeReceiptRecordObserver_RoundTripsWithoutQuestionText()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "router.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = path }));
        database.EnsureCreated();

        var store = new SqliteGradeReceiptStore(database);
        var windowCache = new PendingConversationWindowCache(Options.Create(new JudgeOptions()));
        var secretPath = Path.Combine(Path.GetDirectoryName(path)!, "secrets.dat");
        var hasher = new QuestionHmacHasher(new ProtectedSecretStore(secretPath));
        const string question = "Fix the null reference in Program.cs";
        windowCache.Set(correlationId: "sess-1:1",
            window: new ConversationWindowResult(
                FormattedText: $"Task:\n{question}",
                NewestUserMessage: question,
                QuestionChars: question.Length,
                ContextMessages: 0,
                Truncated: false));

        var observer = new GradeReceiptRecordObserver(
            store: store,
            pendingWindowCache: windowCache,
            questionHmacHasher: hasher,
            qualityOptions: Options.Create(new QualityOptions { ScorerVersion = "3.0", WeightsVersion = "3.0-judge-heavy" }),
            judgeOptions: Options.Create(new JudgeOptions { PromptVersion = "g-eval-v2-question-rubric" }),
            logger: NullLogger<GradeReceiptRecordObserver>.Instance);

        await observer.ObserveAsync(result: new QualityResult
        {
            RequestCorrelationId = "sess-1:1",
            Model = "gpt-5.4",
            Dimension = "code_generation",
            UnifiedScore = 0.8,
            GradeStatus = GradeStatus.Complete,
            JudgeScore = 0.7
        }, cancellationToken: TestContext.Current.CancellationToken);

        var roundTrip = await store.TryGetAsync(correlationId: "sess-1:1",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(roundTrip);
        Assert.Equal(expected: GradeStatus.Complete, actual: roundTrip!.GradeStatus);
        Assert.NotNull(roundTrip.QuestionHmac);
        Assert.DoesNotContain(question, roundTrip.QuestionHmac, StringComparison.Ordinal);
        Assert.DoesNotContain(question, roundTrip.AbstainedGraders, StringComparison.Ordinal);

        using var connection = database.OpenConnection();
        using var scan = connection.CreateCommand();
        scan.CommandText = """
                           SELECT correlation_id, dimension, model, grade_status, question_hmac, scorer_version,
                                  prompt_version, weights_version, abstained_graders
                           FROM grade_receipts;
                           """;
        using var reader = scan.ExecuteReader();
        Assert.True(reader.Read());
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.IsDBNull(i)) continue;
            var value = reader.GetValue(i).ToString() ?? string.Empty;
            Assert.DoesNotContain(question, value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ScoreTableRetentionService_PurgesGradeReceiptRows()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "router.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = path }));
        database.EnsureCreated();
        var store = new SqliteGradeReceiptStore(database);

        for (var i = 0; i < 5; i++)
            await store.UpsertAsync(record: SampleReceipt(correlationId: $"c-{i}"),
                cancellationToken: TestContext.Current.CancellationToken);

        var service = new ScoreTableRetentionService(
            logger: NullLogger<ScoreTableRetentionService>.Instance,
            store: store,
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions { GraderScoreMaxRows = 2, GraderScoreRetentionDays = 30 }),
            isEnabled: static _ => true,
            maxRows: static o => o.GraderScoreMaxRows,
            retentionDays: static o => o.GraderScoreRetentionDays,
            tableLabel: "Grade-receipt");

        await service.CheckAndPurgeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, actual: await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EnsureCreated_PurgeRemovesLiveRowsAndKeepsBenchmarkTallies()
    {
        var path = CreateLegacyDatabaseWithoutIssue293Tables();
        SeedLiveAndBenchmarkRows(path);

        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = path }));
        database.EnsureCreated();
        database.EnsureCreated();

        using var connection = database.OpenConnection();
        Assert.Equal(expected: 0L, actual: Scalar(connection, "SELECT COUNT(*) FROM memory_entries;"));
        Assert.Equal(expected: 0L, actual: Scalar(connection, "SELECT COUNT(*) FROM grader_scores;"));
        Assert.Equal(expected: 1L, actual: Scalar(connection, "SELECT COUNT(*) FROM dimension_scores WHERE dimension = 'code_generation';"));
        Assert.Equal(expected: 0L, actual: Scalar(connection, "SELECT COUNT(*) FROM dimension_scores WHERE dimension LIKE 'live:%';"));
        Assert.True(SchemaMetaExists(connection: connection, key: RouterMemoryDatabase.Issue293LiveLearningPurgeKey));
    }

    private static GradeReceiptRecord SampleReceipt(string correlationId) =>
        new(CorrelationId: correlationId, CreatedAtUtc: DateTimeOffset.UtcNow, Dimension: "code_generation",
            Model: "m", GradeStatus: GradeStatus.StructureOnly, UnifiedScore: 0.5, QuestionHmac: "abc",
            QuestionChars: 3, ContextMessages: 0, QuestionTruncated: false, FingerprintRef: null,
            ScorerVersion: "3.0", PromptVersion: null, WeightsVersion: "3.0-judge-heavy", AbstainedGraders: string.Empty);

    private static string CreateLegacyDatabaseWithoutIssue293Tables()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "legacy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE memory_entries (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  embedding BLOB NOT NULL,
                                  chosen_model TEXT NOT NULL,
                                  score REAL NOT NULL,
                                  cost REAL NOT NULL,
                                  verifier_trace TEXT NULL,
                                  created_at_utc TEXT NOT NULL);

                              CREATE TABLE dimension_scores (
                                  dimension TEXT NOT NULL,
                                  model TEXT NOT NULL,
                                  sum REAL NOT NULL,
                                  count INTEGER NOT NULL,
                                  PRIMARY KEY (dimension, model));

                              CREATE TABLE grader_scores (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  correlation_id TEXT NOT NULL,
                                  created_at_utc TEXT NOT NULL,
                                  dimension TEXT NOT NULL,
                                  model TEXT NOT NULL,
                                  grader_key TEXT NOT NULL,
                                  score REAL NOT NULL,
                                  grader_backbone_model TEXT NULL,
                                  response_length_chars INTEGER NULL);

                              CREATE TABLE judge_shadow_scores (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  correlation_id TEXT NOT NULL,
                                  created_at_utc TEXT NOT NULL,
                                  dimension TEXT NOT NULL,
                                  model TEXT NOT NULL,
                                  static_score REAL NOT NULL,
                                  judge_score REAL NOT NULL,
                                  judge_model TEXT NOT NULL,
                                  judge_prompt_version TEXT NOT NULL,
                                  judge_latency_ms INTEGER NOT NULL,
                                  used_logprobs INTEGER NOT NULL,
                                  syntax_authoritative INTEGER NULL);
                              """;
        command.ExecuteNonQuery();
        return path;
    }

    private static void SeedLiveAndBenchmarkRows(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO memory_entries (embedding, chosen_model, score, cost, created_at_utc)
                              VALUES (x'00', 'm', 0.5, 0, '2020-01-01T00:00:00Z');
                              INSERT INTO dimension_scores (dimension, model, sum, count)
                              VALUES ('live:code_generation', 'm', 1, 1),
                                     ('code_generation', 'm', 2, 2);
                              INSERT INTO grader_scores (correlation_id, created_at_utc, dimension, model, grader_key, score)
                              VALUES ('c-1', '2020-01-01T00:00:00Z', 'd', 'm', 'analysis', 0.5);
                              """;
        command.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool SchemaMetaExists(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }
}
