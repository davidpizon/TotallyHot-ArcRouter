using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Quality;
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
        Assert.True(ColumnExists(connection: connection, table: "grade_receipts", column: "incomplete_reason"));
        Assert.True(ColumnExists(connection: connection, table: "grade_receipts", column: "grade_source"));
        Assert.True(ColumnExists(connection: connection, table: "grade_receipts", column: "response_has_code"));
        Assert.True(ColumnExists(connection: connection, table: "grade_receipts", column: "judge_model"));
        Assert.True(ColumnExists(connection: connection, table: "grade_receipts", column: "answer_model"));
        Assert.True(ColumnExists(connection: connection, table: "grade_receipts", column: "question_vector_missing"));
        Assert.True(ColumnExists(connection: connection, table: "response_vectors", column: "vector"));
        Assert.True(ColumnExists(connection: connection, table: "grader_scores", column: "question_hmac"));
        Assert.True(ColumnExists(connection: connection, table: "grader_scores", column: "prompt_version"));
    }

    [Fact]
    public async Task GradeReceiptRecordObserver_RoundTripsWithoutQuestionText()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "router.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = path }));
        database.EnsureCreated();

        IGradeReceiptStore store = new SqliteGradeReceiptStore(database);
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
        Assert.Equal(expected: GradeStatus.Complete, actual: roundTrip.GradeStatus);
        Assert.NotNull(roundTrip.QuestionHmac);
        Assert.DoesNotContain(question, roundTrip.QuestionHmac, StringComparison.Ordinal);
        Assert.DoesNotContain(question, roundTrip.AbstainedGraders, StringComparison.Ordinal);

        await using var connection = database.OpenConnection();
        await using var scan = connection.CreateCommand();
        scan.CommandText = """
                           SELECT correlation_id, dimension, model, grade_status, question_hmac, scorer_version,
                                  prompt_version, weights_version, abstained_graders, incomplete_reason, grade_source,
                                  response_has_code, judge_model, answer_model
                           FROM grade_receipts;
                           """;
        await using var reader = scan.ExecuteReader();
        Assert.True(reader.Read());
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.IsDBNull(i)) continue;
            var value = reader.GetValue(i).ToString() ?? string.Empty;
            Assert.DoesNotContain(question, value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GradeReceiptRecordObserver_BootstrapRow_StampsTeacherSourceAndNoQuestionText()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "router.db");
        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions { EmbeddingMemoryDatabasePath = path }));
        database.EnsureCreated();

        IGradeReceiptStore store = new SqliteGradeReceiptStore(database);
        var windowCache = new PendingConversationWindowCache(Options.Create(new JudgeOptions()));
        var secretPath = Path.Combine(Path.GetDirectoryName(path)!, "secrets.dat");
        var hasher = new QuestionHmacHasher(new ProtectedSecretStore(secretPath));
        const string question = "Explain what a mutex is without writing code";
        windowCache.Set(correlationId: "sess-1:2",
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
            judgeOptions: Options.Create(new JudgeOptions
            {
                PromptVersion = "g-eval-v3-question-rubric-noncode", ModelName = "local-judge"
            }),
            logger: NullLogger<GradeReceiptRecordObserver>.Instance);

        await observer.ObserveAsync(result: new QualityResult
        {
            RequestCorrelationId = "sess-1:2",
            Model = "gpt-5.4",
            Dimension = string.Empty,
            UnifiedScore = 0.8,
            GradeStatus = GradeStatus.Complete,
            JudgeScore = 0.8,
            RecipeId = GradingRecipes.Bootstrap,
            GradeSource = GradeSources.Teacher,
            ResponseHasCode = false
        }, cancellationToken: TestContext.Current.CancellationToken);

        var roundTrip = await store.TryGetAsync(correlationId: "sess-1:2",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(roundTrip);
        Assert.Equal(GradeStatus.Complete, actual: roundTrip.GradeStatus);
        Assert.Equal(expected: GradingRecipes.Bootstrap, actual: roundTrip.WeightsVersion);
        Assert.Equal(expected: GradeSources.Teacher, actual: roundTrip.GradeSource);
        Assert.False(roundTrip.ResponseHasCode);
        Assert.Equal(expected: "local-judge", actual: roundTrip.JudgeModel);
        Assert.Equal(expected: "gpt-5.4", actual: roundTrip.AnswerModel);
        Assert.Null(roundTrip.IncompleteReason);
        Assert.NotNull(roundTrip.QuestionHmac);
        Assert.DoesNotContain(question, roundTrip.QuestionHmac, StringComparison.Ordinal);
        Assert.Equal(0.8, actual: roundTrip.UnifiedScore);
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

    /// <summary>
    /// Upgrades a file copy of a pre-#293 database. The first open deletes live learning and stamps
    /// <c>schema_meta</c>; a second open leaves rows written after that stamp in place.
    /// </summary>
    [Fact]
    public void EnsureCreated_OnCopiedLegacyDatabase_PurgesLiveLearningOnce()
    {
        var source = CreateLegacyDatabaseWithoutIssue293Tables();
        SeedLiveAndBenchmarkRows(source);
        SeedShadowScore(source);

        var copy = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "router_embedding_memory.db");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(sourceFileName: source, destFileName: copy);

        var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
        {
            EmbeddingMemoryDatabasePath = copy
        }));
        database.EnsureCreated();

        using (var upgraded = database.OpenConnection())
        {
            Assert.Equal(expected: 0L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM memory_entries;"));
            Assert.Equal(expected: 0L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM grader_scores;"));
            Assert.Equal(expected: 0L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM judge_shadow_scores;"));
            Assert.Equal(expected: 0L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM dimension_scores WHERE dimension LIKE 'live:%';"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM dimension_scores WHERE dimension = 'code_generation';"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded,
                "SELECT COUNT(*) FROM schema_meta WHERE key = 'issue-293-live-learning-purge';"));
        }

        using (var untouched = new SqliteConnection($"Data Source={source}"))
        {
            untouched.Open();
            Assert.Equal(expected: 1L, actual: Scalar(untouched, "SELECT COUNT(*) FROM memory_entries;"));
            Assert.Equal(expected: 1L, actual: Scalar(untouched, "SELECT COUNT(*) FROM grader_scores;"));
            Assert.Equal(expected: 1L, actual: Scalar(untouched, "SELECT COUNT(*) FROM judge_shadow_scores;"));
            Assert.Equal(expected: 1L, actual: Scalar(untouched, "SELECT COUNT(*) FROM dimension_scores WHERE dimension LIKE 'live:%';"));
        }

        using (var upgraded = database.OpenConnection())
        {
            using var insert = upgraded.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO memory_entries (embedding, chosen_model, score, cost, created_at_utc)
                                 VALUES (X'01', 'after', 0.9, 0, '2026-10-21T00:00:00Z');
                                 INSERT INTO dimension_scores (dimension, model, sum, count)
                                 VALUES ('live:code_generation', 'after', 0.9, 1);
                                 INSERT INTO grader_scores (correlation_id, created_at_utc, dimension, model, grader_key, score)
                                 VALUES ('after', '2026-10-21T00:00:00Z', 'd', 'after', 'judge', 0.9);
                                 INSERT INTO judge_shadow_scores (
                                     correlation_id, created_at_utc, dimension, model, static_score, judge_score,
                                     judge_model, judge_prompt_version, judge_latency_ms, used_logprobs)
                                 VALUES ('after', '2026-10-21T00:00:00Z', 'd', 'after', 0.4, 0.9, 'judge', 'v', 1, 0);
                                 """;
            insert.ExecuteNonQuery();
        }

        string marker;
        using (var upgraded = database.OpenConnection())
            marker = Text(upgraded, "SELECT value FROM schema_meta WHERE key = 'issue-293-live-learning-purge';");

        database.EnsureCreated();

        using (var upgraded = database.OpenConnection())
        {
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM memory_entries WHERE chosen_model = 'after';"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM grader_scores;"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM judge_shadow_scores;"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM dimension_scores WHERE dimension LIKE 'live:%';"));
            Assert.Equal(expected: 1L, actual: Scalar(upgraded, "SELECT COUNT(*) FROM dimension_scores WHERE dimension = 'code_generation';"));
            Assert.Equal(expected: marker, actual: Text(upgraded,
                "SELECT value FROM schema_meta WHERE key = 'issue-293-live-learning-purge';"));
        }
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

    private static void SeedShadowScore(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO judge_shadow_scores (
                                  correlation_id, created_at_utc, dimension, model, static_score, judge_score,
                                  judge_model, judge_prompt_version, judge_latency_ms, used_logprobs)
                              VALUES ('c-1', '2020-01-01T00:00:00Z', 'd', 'm', 0.2, 0.4, 'judge', 'v1', 3, 0);
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

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool SchemaMetaExists(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }
}
