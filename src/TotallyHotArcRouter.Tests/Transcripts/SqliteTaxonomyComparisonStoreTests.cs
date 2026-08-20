using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Transcripts;

/// <summary>
/// Covers <see cref="SqliteTaxonomyComparisonStore"/> directly - the regret columns' round-trip, the
/// additive migration for databases created before them (docs/router/routing-roi-regret-plan.md), and the
/// queue predicate's dimension requirement. The service-level drain behavior lives in
/// <see cref="TaxonomyComparisonServiceTests"/>.
/// </summary>
public sealed class SqliteTaxonomyComparisonStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _dbPath;

    public SqliteTaxonomyComparisonStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "transcripts.db");
    }

    [Fact]
    public async Task Upsert_RoundTripsTheRegretColumns()
    {
        var store = CreateStore();

        await store.UpsertAsync(
            MakeRecord(1) with { BaselinePredictedScore = 0.62, EstimatedRegret = -0.135 },
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await store.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0.62, row.BaselinePredictedScore);
        Assert.Equal(-0.135, row.EstimatedRegret);
    }

    [Fact]
    public async Task Upsert_NullRegret_StaysNullThroughTheRoundTrip()
    {
        var store = CreateStore();

        await store.UpsertAsync(
            MakeRecord(1) with { BaselinePredictedScore = null, EstimatedRegret = null },
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await store.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(row.BaselinePredictedScore);
        Assert.Null(row.EstimatedRegret);
    }

    [Fact]
    public async Task EnsureCreated_AddsTheRegretColumnsToAPreRegretDatabase()
    {
        // A database exactly as a pre-regret build left it: the taxonomy_comparisons table exists but has
        // no baseline_predicted_score / estimated_regret columns. EnsureCreated's additive migration must
        // add them, after which writes and reads work against the old file.
        WritePreRegretDatabase();

        var store = CreateStore();
        await store.UpsertAsync(
            MakeRecord(1) with { BaselinePredictedScore = 0.4, EstimatedRegret = 0.2 },
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await store.LoadSinceAsync(
            DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0.4, row.BaselinePredictedScore);
        Assert.Equal(0.2, row.EstimatedRegret);
    }

    [Fact]
    public async Task LoadPendingComparisons_ExcludesRowsWithNoDimension()
    {
        var options = Options.Create(new TranscriptOptions { Enabled = true });
        var database = CreateDatabase();
        var transcriptStore = new SqliteTranscriptStore(database, options);
        var comparisonStore = new SqliteTaxonomyComparisonStore(database, options);
        var token = TestContext.Current.CancellationToken;

        var dimensionless = await SeedScoredLinkedTranscriptAsync(transcriptStore, "corr-1", dimension: null, token);
        var ready = await SeedScoredLinkedTranscriptAsync(transcriptStore, "corr-2", dimension: "code_generation", token);

        var pending = await comparisonStore.LoadPendingComparisonsAsync(10, token);

        // The dimensionless row satisfies every other readiness condition but can never be compared, so
        // admitting it would park it at the head of the oldest-first queue forever.
        Assert.Equal([ready], pending);
        Assert.DoesNotContain(dimensionless, pending);
    }

    /// <summary>Creates the store over a freshly-migrated database at <see cref="_dbPath"/>.</summary>
    private SqliteTaxonomyComparisonStore CreateStore() =>
        new(CreateDatabase(), Options.Create(new TranscriptOptions { Enabled = true }));

    /// <summary>Creates (or migrates) the transcript database file and returns its handle.</summary>
    private TranscriptDatabase CreateDatabase()
    {
        var database = new TranscriptDatabase(Options.Create(new StorageOptions { TranscriptDatabasePath = _dbPath }));
        database.EnsureCreated();
        return database;
    }

    /// <summary>Inserts one scored transcript with a memory-entry link, returning its row id.</summary>
    private static async Task<long> SeedScoredLinkedTranscriptAsync(
        SqliteTranscriptStore store, string correlationId, string? dimension, CancellationToken token)
    {
        var id = await store.InsertAsync(
            new TranscriptRecord(
                Id: 0,
                CorrelationId: correlationId,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                RequestedModel: "auto",
                RoutedModel: "model-a",
                Dimension: dimension,
                Difficulty: null,
                Language: null,
                IsUtility: false,
                PromptText: "p",
                ResponseText: "r",
                Score: null,
                Cost: 0.01m,
                IsExploratory: false,
                Propensity: 1.0,
                InputTokens: 10,
                OutputTokens: 5,
                MemoryEntryId: null,
                DimBestModel: "model-b"),
            token);
        await store.LinkMemoryEntryAsync(id!.Value, memoryEntryId: 1, token);
        await store.UpdateOutcomeAsync(correlationId, 0.8, token);
        return id.Value;
    }

    /// <summary>A minimal but fully-populated comparison row for round-trip assertions.</summary>
    private static TaxonomyComparisonRecord MakeRecord(long transcriptId) =>
        new(
            TranscriptId: transcriptId,
            ComparedAtUtc: DateTimeOffset.UtcNow,
            SessionId: "session-1",
            ObservedScore: 0.8,
            DimensionPredictedScore: 0.7,
            ClusterPredictedScore: null,
            DimensionAbsoluteError: 0.1,
            ClusterAbsoluteError: null,
            IsClustered: false,
            IsExploratory: false,
            RoutedModel: "model-a",
            BaselineModel: "model-b",
            ActualCostUsd: 0.01m,
            BaselineEstimatedCostUsd: 0.03m,
            EstimatedNetSavingsUsd: 0.02m,
            BaselinePredictedScore: null,
            EstimatedRegret: null);

    /// <summary>
    /// Writes a database file with the exact table shapes a pre-regret (Phase T4) build created, so the
    /// migration test exercises the real "old file, new code" path rather than a synthetic one.
    /// </summary>
    private void WritePreRegretDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
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

            CREATE TABLE taxonomy_comparisons (
                transcript_id             INTEGER PRIMARY KEY,
                compared_at_utc           TEXT    NOT NULL,
                session_id                TEXT    NOT NULL,
                observed_score            REAL    NOT NULL,
                dimension_predicted_score REAL    NULL,
                cluster_predicted_score   REAL    NULL,
                dimension_abs_error       REAL    NULL,
                cluster_abs_error         REAL    NULL,
                is_clustered              INTEGER NOT NULL,
                is_exploratory            INTEGER NOT NULL,
                routed_model              TEXT    NOT NULL,
                baseline_model            TEXT    NULL,
                actual_cost_usd           REAL    NULL,
                baseline_estimated_cost_usd REAL  NULL,
                estimated_net_savings_usd REAL    NULL
            );
            """;
        command.ExecuteNonQuery();
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
