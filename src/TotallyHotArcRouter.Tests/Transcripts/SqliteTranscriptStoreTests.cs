using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Transcripts;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="TranscriptDatabase"/> and <see cref="SqliteTranscriptStore"/>
/// (docs/router/self-organizing-classification-plan.md Phase T1a/T1b): the insert-then-backfill-score
/// round-trip, and the enabled/disabled-capture behavior - "with capture disabled (the default), no table
/// is created and nothing is written" is the plan's stated exit criterion.
/// </summary>
public class SqliteTranscriptStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _dbPath;

    public SqliteTranscriptStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDirectory, "transcripts.db");
    }

    [Fact]
    public async Task InsertAsync_ThenUpdateOutcomeAsync_RoundTripsTheScoreBackfill()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        var store = new SqliteTranscriptStore(database, Options.Create(new TranscriptOptions { Enabled = true }));

        var record = MakeRecord("corr-1");
        var id = await store.InsertAsync(record, TestContext.Current.CancellationToken);
        Assert.NotNull(id);

        await store.UpdateOutcomeAsync("corr-1", 0.83, TestContext.Current.CancellationToken);

        var row = ReadRow(database, "corr-1");
        Assert.Equal("gpt-5.4", row.RequestedModel);
        Assert.Equal("kimi-k2.5", row.RoutedModel);
        Assert.Equal(0.83, row.Score!.Value, 6);
        Assert.Equal(0.0042, (double)row.Cost!.Value, 6);
        Assert.True(row.IsExploratory);
        Assert.Equal(0.05, row.Propensity, 6);
    }

    [Fact]
    public async Task InsertAsync_ScoreLeftNullUntilBackfilled()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        var store = new SqliteTranscriptStore(database, Options.Create(new TranscriptOptions { Enabled = true }));

        await store.InsertAsync(MakeRecord("corr-2"), TestContext.Current.CancellationToken);

        var row = ReadRow(database, "corr-2");
        Assert.Null(row.Score);
    }

    [Fact]
    public async Task InsertAsync_CaptureDisabled_ReturnsNullAndWritesNothing()
    {
        var database = CreateDatabase();
        // Capture disabled: EnsureCreated deliberately not called here, mirroring how the real startup
        // path skips schema creation when TranscriptOptions.Enabled is false.
        var store = new SqliteTranscriptStore(database, Options.Create(new TranscriptOptions { Enabled = false }));

        var id = await store.InsertAsync(MakeRecord("corr-3"), TestContext.Current.CancellationToken);

        Assert.Null(id);
        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public async Task UpdateOutcomeAsync_CaptureDisabled_IsANoOp()
    {
        var database = CreateDatabase();
        var store = new SqliteTranscriptStore(database, Options.Create(new TranscriptOptions { Enabled = false }));

        await store.UpdateOutcomeAsync("corr-4", 0.5, TestContext.Current.CancellationToken);

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
        var store = new SqliteTranscriptStore(database, Options.Create(new TranscriptOptions { Enabled = true }));

        var id = await store.InsertAsync(
            MakeRecord("corr-dimbest") with { DimBestModel = "glm-5" }, TestContext.Current.CancellationToken);

        var row = await store.GetTranscriptAsync(id!.Value, TestContext.Current.CancellationToken);
        Assert.Equal("glm-5", row!.DimBestModel);
    }

    [Fact]
    public async Task InsertAsync_DimBestAbstained_StoresNullRatherThanTheServedModel()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        var store = new SqliteTranscriptStore(database, Options.Create(new TranscriptOptions { Enabled = true }));

        // An abstention means the frozen baseline expressed no preference. Defaulting it to the served
        // model would fabricate a zero-savings counterfactual out of a decision nobody made.
        var id = await store.InsertAsync(MakeRecord("corr-abstain"), TestContext.Current.CancellationToken);

        var row = await store.GetTranscriptAsync(id!.Value, TestContext.Current.CancellationToken);
        Assert.Null(row!.DimBestModel);
    }

    [Fact]
    public async Task EnsureCreated_DatabasePredatingPhaseT4_GainsTheDimBestColumn()
    {
        // A transcript database written by a Phase T1 build already exists with the older shape, and
        // CREATE TABLE IF NOT EXISTS is blind to it - without the explicit PRAGMA migration every read
        // would fail with "no such column".
        Directory.CreateDirectory(_tempDirectory);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
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
        var store = new SqliteTranscriptStore(database, Options.Create(new TranscriptOptions { Enabled = true }));

        var id = await store.InsertAsync(
            MakeRecord("corr-migrated") with { DimBestModel = "glm-5" }, TestContext.Current.CancellationToken);

        var row = await store.GetTranscriptAsync(id!.Value, TestContext.Current.CancellationToken);
        Assert.Equal("glm-5", row!.DimBestModel);
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
        await store.InsertAsync(MakeRecord("corr-never"), TestContext.Current.CancellationToken);

        var pending = await store.LoadPendingQualityRescanAsync("v2", 10, TestContext.Current.CancellationToken);

        Assert.Single(pending);
        Assert.Null(ReadScorerVersion(database, "corr-never"));
    }

    [Fact]
    public async Task LoadPendingQualityRescanAsync_ExcludesRowsAlreadyAtTheCurrentVersion()
    {
        var (_, store) = CreateEnabledStore();
        var id = await store.InsertAsync(MakeRecord("corr-current"), TestContext.Current.CancellationToken);
        await store.MarkQualityRescannedAsync(id!.Value, "v2", 0.7, TestContext.Current.CancellationToken);

        var pending = await store.LoadPendingQualityRescanAsync("v2", 10, TestContext.Current.CancellationToken);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task LoadPendingQualityRescanAsync_ReturnsRowsStampedByAnOlderScorer()
    {
        var (_, store) = CreateEnabledStore();
        var id = await store.InsertAsync(MakeRecord("corr-stale"), TestContext.Current.CancellationToken);
        await store.MarkQualityRescannedAsync(id!.Value, "v1", 0.7, TestContext.Current.CancellationToken);

        var pending = await store.LoadPendingQualityRescanAsync("v2", 10, TestContext.Current.CancellationToken);

        Assert.Equal([id.Value], pending);
    }

    [Fact]
    public async Task LoadPendingQualityRescanAsync_ExcludesRowsCarryingNoResponseText()
    {
        // Nothing to grade, and returning them would let a run of text-less rows consume an entire
        // bounded batch and starve the sweep of rows it could actually score.
        var (_, store) = CreateEnabledStore();
        await store.InsertAsync(
            MakeRecord("corr-no-text") with { ResponseText = null },
            TestContext.Current.CancellationToken);

        var pending = await store.LoadPendingQualityRescanAsync("v2", 10, TestContext.Current.CancellationToken);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task MarkQualityRescannedAsync_WritesBothTheScoreAndTheVersionStamp()
    {
        var (database, store) = CreateEnabledStore();
        var id = await store.InsertAsync(MakeRecord("corr-mark"), TestContext.Current.CancellationToken);

        await store.MarkQualityRescannedAsync(id!.Value, "v2", 0.61, TestContext.Current.CancellationToken);

        Assert.Equal(0.61, ReadRow(database, "corr-mark").Score!.Value, 6);
        Assert.Equal("v2", ReadScorerVersion(database, "corr-mark"));
    }

    [Fact]
    public async Task MarkQualityRescannedAsync_NullScoreStillStampsSoTheRowLeavesThePendingSet()
    {
        // A row whose text carries no code block is ungradable, but must still be stamped - otherwise the
        // oldest-first sweep returns it again on every tick, forever.
        var (_, store) = CreateEnabledStore();
        var id = await store.InsertAsync(MakeRecord("corr-ungradable"), TestContext.Current.CancellationToken);

        await store.MarkQualityRescannedAsync(id!.Value, "v2", score: null, TestContext.Current.CancellationToken);

        Assert.Empty(await store.LoadPendingQualityRescanAsync("v2", 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadPendingQualityRescanAsync_CaptureDisabled_ReturnsEmptyAndWritesNothing()
    {
        var database = CreateDatabase();
        var store = new SqliteTranscriptStore(database, Options.Create(new TranscriptOptions { Enabled = false }));

        var pending = await store.LoadPendingQualityRescanAsync("v2", 10, TestContext.Current.CancellationToken);

        Assert.Empty(pending);
        Assert.False(File.Exists(_dbPath));
    }

    private (TranscriptDatabase Database, SqliteTranscriptStore Store) CreateEnabledStore()
    {
        var database = CreateDatabase();
        database.EnsureCreated();
        return (database, new SqliteTranscriptStore(database, Options.Create(new TranscriptOptions { Enabled = true })));
    }

    private static string? ReadScorerVersion(TranscriptDatabase database, string correlationId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT scorer_version FROM request_transcripts WHERE correlation_id = $correlationId;";
        command.Parameters.AddWithValue("$correlationId", correlationId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    private TranscriptDatabase CreateDatabase() =>
        new(Options.Create(new StorageOptions { TranscriptDatabasePath = _dbPath }));

    private static TranscriptRecord MakeRecord(string correlationId) =>
        new(
            Id: 0,
            CorrelationId: correlationId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RequestedModel: "gpt-5.4",
            RoutedModel: "kimi-k2.5",
            Dimension: "bug_fixing",
            Difficulty: "medium",
            Language: "python",
            IsUtility: false,
            PromptText: "fix this bug",
            ResponseText: "here is the fix",
            Score: null,
            Cost: 0.0042m,
            IsExploratory: true,
            Propensity: 0.05,
            InputTokens: 100,
            OutputTokens: 50,
            MemoryEntryId: null);

    private static (string RequestedModel, string RoutedModel, double? Score, decimal? Cost, bool IsExploratory, double Propensity) ReadRow(
        TranscriptDatabase database, string correlationId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT requested_model, routed_model, score, cost, is_exploratory, propensity
            FROM request_transcripts
            WHERE correlation_id = $correlationId;
            """;
        command.Parameters.AddWithValue("$correlationId", correlationId);
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

    public void Dispose()
    {
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
