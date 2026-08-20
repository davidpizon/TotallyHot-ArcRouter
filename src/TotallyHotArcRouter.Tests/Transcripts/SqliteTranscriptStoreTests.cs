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
