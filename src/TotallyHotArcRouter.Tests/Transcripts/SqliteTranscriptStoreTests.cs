using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Tests.TestSupport;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="TranscriptDatabase"/> and <see cref="SqliteTranscriptStore"/>
/// (docs/router/self-organizing-classification-plan.md Phase T1a/T1b): the insert-then-backfill-score
/// round-trip, the enabled/disabled-capture behavior - "with capture disabled, no table is created and
/// nothing is written" - and the live toggle (capture switched on after construction starts writing, and
/// lazily creates the table, without a restart).
/// </summary>
public class SqliteTranscriptStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _tempDirectory;

    public SqliteTranscriptStoreTests()
    {
        _tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(path1: _tempDirectory, path2: "transcripts.db");
    }

    public void Dispose()
    {
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
    public async Task InsertAsync_ThenUpdateOutcomeAsync_RoundTripsTheScoreBackfill()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }));

        var record = MakeRecord("corr-1");
        var id = await store.InsertAsync(record: record, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(id);

        await store.UpdateOutcomeAsync(correlationId: "corr-1", 0.83,
            cancellationToken: TestContext.Current.CancellationToken);

        var row = ReadRow(database: database, correlationId: "corr-1");
        Assert.Equal(expected: "gpt-5.4", actual: row.RequestedModel);
        Assert.Equal(expected: "kimi-k2.5", actual: row.RoutedModel);
        Assert.Equal(0.83, actual: row.Score!.Value, 6);
        Assert.Equal(0.0042, actual: (double)row.Cost!.Value, 6);
        Assert.True(row.IsExploratory);
        Assert.Equal(0.05, actual: row.Propensity, 6);
    }

    [Fact]
    public async Task InsertAsync_ScoreLeftNullUntilBackfilled()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }));

        await store.InsertAsync(record: MakeRecord("corr-2"), cancellationToken: TestContext.Current.CancellationToken);

        var row = ReadRow(database: database, correlationId: "corr-2");
        Assert.Null(row.Score);
    }

    [Fact]
    public async Task InsertAsync_CaptureDisabled_ReturnsNullAndWritesNothing()
    {
        var database = CreateDatabase();
        // Capture disabled: EnsureCreated deliberately not called here, mirroring how the real startup
        // path skips schema creation when TranscriptOptions.Enabled is false.
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = false }));

        var id = await store.InsertAsync(record: MakeRecord("corr-3"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(id);
        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public async Task UpdateOutcomeAsync_CaptureDisabled_IsANoOp()
    {
        var database = CreateDatabase();
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = false }));

        await store.UpdateOutcomeAsync(correlationId: "corr-4", 0.5,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public void EnsureCreated_NotCalled_NoDatabaseFileExists()
    {
        CreateDatabase();

        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public async Task InsertAsync_RoundTripsTheDimBestCounterfactual()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }));

        var id = await store.InsertAsync(
            record: MakeRecord("corr-dimbest") with { DimBestModel = "glm-5" },
            cancellationToken: TestContext.Current.CancellationToken);

        var row = await store.GetTranscriptAsync(id: id!.Value,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected: "glm-5", actual: row!.DimBestModel);
    }

    [Fact]
    public async Task InsertAsync_DimBestAbstained_StoresNullRatherThanTheServedModel()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }));

        // An abstention means the frozen baseline expressed no preference. Defaulting it to the served
        // model would fabricate a zero-savings counterfactual out of a decision nobody made.
        var id = await store.InsertAsync(record: MakeRecord("corr-abstain"),
            cancellationToken: TestContext.Current.CancellationToken);

        var row = await store.GetTranscriptAsync(id: id!.Value,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(row!.DimBestModel);
    }

    [Fact]
    public async Task EnsureCreated_DatabasePredatingPhaseT4_GainsTheDimBestColumn()
    {
        // A transcript database written by a Phase T1 build already exists with the older shape, and
        // CREATE TABLE IF NOT EXISTS is blind to it - without the explicit PRAGMA migration every read
        // would fail with "no such column".
        Directory.CreateDirectory(_tempDirectory);
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            await using var create = connection.CreateCommand();
            create.CommandText = """
                                 CREATE TABLE request_transcripts (
                                     id INTEGER PRIMARY KEY AUTOINCREMENT, correlation_id TEXT NOT NULL,
                                     created_at_utc TEXT NOT NULL, requested_model TEXT NOT NULL, routed_model TEXT NOT NULL,
                                     dimension TEXT NULL, difficulty TEXT NULL, language TEXT NULL, is_utility INTEGER NOT NULL,
                                     prompt_text TEXT NULL, response_text TEXT NULL, score REAL NULL, cost REAL NULL,
                                     is_exploratory INTEGER NOT NULL, propensity REAL NOT NULL, input_tokens INTEGER NULL,
                                     output_tokens INTEGER NULL, memory_entry_id INTEGER NULL);
                                 """;
            create.ExecuteNonQuery();
        }

        var database = CreateDatabase();
        database.EnsureCreated();
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }));

        var id = await store.InsertAsync(
            record: MakeRecord("corr-migrated") with { DimBestModel = "glm-5" },
            cancellationToken: TestContext.Current.CancellationToken);

        var row = await store.GetTranscriptAsync(id: id!.Value,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected: "glm-5", actual: row!.DimBestModel);
    }

    [Fact]
    public void EnsureCreated_RunTwice_IsIdempotent()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        database.EnsureCreated();

        // The second pass must not try to re-add a column that is already there.
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task LoadPendingQualityRescanAsync_ReturnsRowsThatWereNeverScanned()
    {
        // The load-bearing case for the query's `IS NOT` operator. SQL's three-valued logic evaluates
        // `NULL <> 'v2'` to NULL rather than true, so a plain inequality would silently exclude every
        // never-scanned row - which is the entire population the first sweep exists to grade.
        var (database, store) = CreateEnabledStore();
        await store.InsertAsync(record: MakeRecord("corr-never"),
            cancellationToken: TestContext.Current.CancellationToken);

        var pending = await store.LoadPendingQualityRescanAsync(scorerVersion: "v2", 10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(pending);
        Assert.Null(ReadScorerVersion(database: database, correlationId: "corr-never"));
    }

    [Fact]
    public async Task LoadPendingQualityRescanAsync_ExcludesRowsAlreadyAtTheCurrentVersion()
    {
        var (_, store) = CreateEnabledStore();
        var id = await store.InsertAsync(record: MakeRecord("corr-current"),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.MarkQualityRescannedAsync(transcriptId: id!.Value, scorerVersion: "v2", 0.7,
            cancellationToken: TestContext.Current.CancellationToken);

        var pending = await store.LoadPendingQualityRescanAsync(scorerVersion: "v2", 10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task LoadPendingQualityRescanAsync_ReturnsRowsStampedByAnOlderScorer()
    {
        var (_, store) = CreateEnabledStore();
        var id = await store.InsertAsync(record: MakeRecord("corr-stale"),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.MarkQualityRescannedAsync(transcriptId: id!.Value, scorerVersion: "v1", 0.7,
            cancellationToken: TestContext.Current.CancellationToken);

        var pending = await store.LoadPendingQualityRescanAsync(scorerVersion: "v2", 10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: [id.Value], actual: pending);
    }

    [Fact]
    public async Task LoadPendingQualityRescanAsync_ExcludesRowsCarryingNoResponseText()
    {
        // Nothing to grade, and returning them would let a run of text-less rows consume an entire
        // bounded batch and starve the sweep of rows it could actually score.
        var (_, store) = CreateEnabledStore();
        await store.InsertAsync(
            record: MakeRecord("corr-no-text") with { ResponseText = null },
            cancellationToken: TestContext.Current.CancellationToken);

        var pending = await store.LoadPendingQualityRescanAsync(scorerVersion: "v2", 10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task MarkQualityRescannedAsync_WritesBothTheScoreAndTheVersionStamp()
    {
        var (database, store) = CreateEnabledStore();
        var id = await store.InsertAsync(record: MakeRecord("corr-mark"),
            cancellationToken: TestContext.Current.CancellationToken);

        await store.MarkQualityRescannedAsync(transcriptId: id!.Value, scorerVersion: "v2", 0.61,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0.61, actual: ReadRow(database: database, correlationId: "corr-mark").Score!.Value, 6);
        Assert.Equal(expected: "v2", actual: ReadScorerVersion(database: database, correlationId: "corr-mark"));
    }

    [Fact]
    public async Task MarkQualityRescannedAsync_NullScoreStillStampsSoTheRowLeavesThePendingSet()
    {
        // A row whose text carries no code block is ungradable, but must still be stamped - otherwise the
        // oldest-first sweep returns it again on every tick, forever.
        var (_, store) = CreateEnabledStore();
        var id = await store.InsertAsync(record: MakeRecord("corr-ungradable"),
            cancellationToken: TestContext.Current.CancellationToken);

        await store.MarkQualityRescannedAsync(transcriptId: id!.Value, scorerVersion: "v2", null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(await store.LoadPendingQualityRescanAsync(scorerVersion: "v2", 10,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadPendingQualityRescanAsync_CaptureDisabled_ReturnsEmptyAndWritesNothing()
    {
        var database = CreateDatabase();
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = false }));

        var pending = await store.LoadPendingQualityRescanAsync(scorerVersion: "v2", 10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(pending);
        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public async Task InsertAsync_EnabledToggledLiveAfterConstruction_StartsWritingWithoutRestart()
    {
        var database = CreateDatabase();
        var options = new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = false });
        var store = new SqliteTranscriptStore(database: database, options: options);

        var firstAttempt = await store.InsertAsync(record: MakeRecord("corr-live-off"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(firstAttempt);
        Assert.False(File.Exists(_dbPath));

        options.Set(new TranscriptOptions { Enabled = true });
        var secondAttempt = await store.InsertAsync(record: MakeRecord("corr-live-on"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(secondAttempt);
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task InsertAsync_WritesSessionIdParsedFromCorrelationId()
    {
        var (database, store) = CreateEnabledStore();
        await store.InsertAsync(record: MakeRecord("sess-A:2"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "sess-A", actual: ReadSessionId(database: database, correlationId: "sess-A:2"));
    }

    [Fact]
    public async Task InsertAsync_CorrelationIdWithNoTurnSuffix_SessionIdIsTheWholeCorrelationId()
    {
        var (database, store) = CreateEnabledStore();
        await store.InsertAsync(record: MakeRecord("standalone-corr"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "standalone-corr",
            actual: ReadSessionId(database: database, correlationId: "standalone-corr"));
    }

    [Fact]
    public async Task ListSessionsAsync_CaptureDisabled_ReturnsEmpty()
    {
        var database = CreateDatabase();
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = false }));

        var rows = await store.ListSessionsAsync(10, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ListSessionsAsync_ReturnsRowsNewestFirstWithSessionIdAndTrainingLinkage()
    {
        var (_, store) = CreateEnabledStore();
        await store.InsertAsync(record: MakeRecord("sess-B:1") with { MemoryEntryId = null },
            cancellationToken: TestContext.Current.CancellationToken);
        var secondId = await store.InsertAsync(record: MakeRecord("sess-B:2") with { MemoryEntryId = 42 },
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = await store.ListSessionsAsync(10, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, actual: rows.Count);
        // Newest first: the second insert (id == secondId) comes back before the first.
        Assert.Equal(expected: secondId, actual: rows[0].Id);
        Assert.Equal(expected: "sess-B", actual: rows[0].SessionId);
        Assert.Equal(expected: "sess-B:2", actual: rows[0].CorrelationId);
        Assert.Equal(42, actual: rows[0].MemoryEntryId);
        Assert.Null(rows[1].MemoryEntryId);
    }

    [Fact]
    public async Task ListSessionsAsync_RespectsLimit()
    {
        var (_, store) = CreateEnabledStore();
        await store.InsertAsync(record: MakeRecord("sess-C:1"),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.InsertAsync(record: MakeRecord("sess-C:2"),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.InsertAsync(record: MakeRecord("sess-C:3"),
            cancellationToken: TestContext.Current.CancellationToken);

        var rows = await store.ListSessionsAsync(2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, actual: rows.Count);
    }

    [Fact]
    public void EnsureCreated_ExistingDatabaseMissingSessionIdColumn_BackfillsFromCorrelationId()
    {
        var database = CreateDatabase();
        Directory.CreateDirectory(_tempDirectory);

        // Simulate a database created by a pre-Phase-1 build: request_transcripts exists but has no
        // session_id column at all, and already carries rows with turn-suffixed correlation ids.
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                                 CREATE TABLE request_transcripts (
                                     id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                                     correlation_id     TEXT    NOT NULL,
                                     created_at_utc     TEXT    NOT NULL,
                                     requested_model    TEXT    NOT NULL,
                                     routed_model       TEXT    NOT NULL,
                                     dimension          TEXT    NULL,
                                     difficulty         TEXT    NULL,
                                     language           TEXT    NULL,
                                     is_utility         INTEGER NOT NULL,
                                     prompt_text        TEXT    NULL,
                                     response_text      TEXT    NULL,
                                     score              REAL    NULL,
                                     cost               REAL    NULL,
                                     is_exploratory     INTEGER NOT NULL,
                                     propensity         REAL    NOT NULL,
                                     input_tokens       INTEGER NULL,
                                     output_tokens      INTEGER NULL,
                                     memory_entry_id    INTEGER NULL,
                                     dim_best_model     TEXT    NULL
                                 );
                                 """;
            create.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO request_transcripts (
                                     correlation_id, created_at_utc, requested_model, routed_model, is_utility,
                                     is_exploratory, propensity)
                                 VALUES ('pre-migration-sess:3', '2026-01-01T00:00:00Z', 'gpt-5.4', 'kimi-k2.5', 0, 0, 0.1);
                                 """;
            insert.ExecuteNonQuery();
        }

        database.EnsureCreated();

        Assert.Equal(expected: "pre-migration-sess",
            actual: ReadSessionId(database: database, correlationId: "pre-migration-sess:3"));
    }

    private static string ReadSessionId(TranscriptDatabase database, string correlationId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM request_transcripts WHERE correlation_id = $correlationId;";
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: correlationId);
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task DeleteAllAsync_RemovesEveryRow()
    {
        var (_, store) = CreateEnabledStore();
        await store.InsertAsync(record: MakeRecord("corr-clear-1"),
            cancellationToken: TestContext.Current.CancellationToken);
        await store.InsertAsync(record: MakeRecord("corr-clear-2"),
            cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await store.DeleteAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, actual: deleted);
        Assert.Equal(0, actual: await store.GetRowCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAllAsync_CaptureCurrentlyDisabled_StillDeletesExistingRows()
    {
        var database = CreateDatabase();
        var options = new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true });
        var store = new SqliteTranscriptStore(database: database, options: options);
        await store.InsertAsync(record: MakeRecord("corr-clear-disabled"),
            cancellationToken: TestContext.Current.CancellationToken);

        // The operator switched capture off after collecting this row, but still wants to flush it.
        options.Set(new TranscriptOptions { Enabled = false });
        var deleted = await store.DeleteAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: deleted);
    }

    [Fact]
    public async Task DeleteAllAsync_NoDatabaseEverCreated_ReturnsZeroWithoutThrowing()
    {
        var database = CreateDatabase();
        var store = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = false }));

        var deleted = await store.DeleteAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: deleted);
    }

    private (TranscriptDatabase Database, SqliteTranscriptStore Store) CreateEnabledStore()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        return (database,
            new SqliteTranscriptStore(database: database,
                options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true })));
    }

    private static string? ReadScorerVersion(TranscriptDatabase database, string correlationId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT scorer_version FROM request_transcripts WHERE correlation_id = $correlationId;";
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: correlationId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    private TranscriptDatabase CreateDatabase()
    {
        return new TranscriptDatabase(Options.Create(new StorageOptions { TranscriptDatabasePath = _dbPath }));
    }

    private static TranscriptRecord MakeRecord(string correlationId)
    {
        return new TranscriptRecord(
            0,
            CorrelationId: correlationId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RequestedModel: "gpt-5.4",
            RoutedModel: "kimi-k2.5",
            Dimension: "bug_fixing",
            Difficulty: "medium",
            Language: "python",
            false,
            PromptText: "fix this bug",
            ResponseText: "here is the fix",
            null,
            0.0042m,
            true,
            0.05,
            100,
            50,
            null);
    }

    private static (string RequestedModel, string RoutedModel, double? Score, decimal? Cost, bool IsExploratory, double
        Propensity) ReadRow(
            TranscriptDatabase database, string correlationId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT requested_model, routed_model, score, cost, is_exploratory, propensity
                              FROM request_transcripts
                              WHERE correlation_id = $correlationId;
                              """;
        command.Parameters.AddWithValue(parameterName: "$correlationId", value: correlationId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        var requestedModel = reader.GetString(0);
        var routedModel = reader.GetString(1);
        var score = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
        var cost = reader.IsDBNull(3) ? (decimal?)null : (decimal)reader.GetDouble(3);
        var isExploratory = reader.GetInt64(4) != 0;
        var propensity = reader.GetDouble(5);

        return (requestedModel, routedModel, score, cost, isExploratory, propensity);
    }
}