using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Tests.TestSupport;
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
    private readonly string _dbPath;
    private readonly string _tempDirectory;

    public SqliteTaxonomyComparisonStoreTests()
    {
        _tempDirectory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
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
    public async Task Upsert_RoundTripsTheRegretColumns()
    {
        var store = CreateStore();

        await store.UpsertAsync(
            record: MakeRecord(1) with { BaselinePredictedScore = 0.62, EstimatedRegret = -0.135 },
            cancellationToken: TestContext.Current.CancellationToken);

        var row = Assert.Single(await store.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0.62, actual: row.BaselinePredictedScore);
        Assert.Equal(-0.135, actual: row.EstimatedRegret);
    }

    [Fact]
    public async Task Upsert_NullRegret_StaysNullThroughTheRoundTrip()
    {
        var store = CreateStore();

        await store.UpsertAsync(
            record: MakeRecord(1) with { BaselinePredictedScore = null, EstimatedRegret = null },
            cancellationToken: TestContext.Current.CancellationToken);

        var row = Assert.Single(await store.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
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
            record: MakeRecord(1) with { BaselinePredictedScore = 0.4, EstimatedRegret = 0.2 },
            cancellationToken: TestContext.Current.CancellationToken);

        var row = Assert.Single(await store.LoadSinceAsync(
            since: DateTimeOffset.MinValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0.4, actual: row.BaselinePredictedScore);
        Assert.Equal(0.2, actual: row.EstimatedRegret);
    }

    [Fact]
    public async Task LoadPendingComparisons_ExcludesRowsWithNoDimension()
    {
        var options = Options.Create(new TranscriptOptions { Enabled = true });
        var database = CreateDatabase();
        var transcriptStore = new SqliteTranscriptStore(database: database,
            options: new StaticOptionsMonitor<TranscriptOptions>(new TranscriptOptions { Enabled = true }));
        var comparisonStore = new SqliteTaxonomyComparisonStore(database: database, options: options);
        var token = TestContext.Current.CancellationToken;

        var dimensionless =
            await SeedScoredLinkedTranscriptAsync(store: transcriptStore, correlationId: "corr-1", null, token: token);
        var ready = await SeedScoredLinkedTranscriptAsync(store: transcriptStore, correlationId: "corr-2",
            dimension: "code_generation", token: token);

        var pending = await comparisonStore.LoadPendingComparisonsAsync(10, cancellationToken: token);

        // The dimensionless row satisfies every other readiness condition but can never be compared, so
        // admitting it would park it at the head of the oldest-first queue forever.
        Assert.Equal(expected: [ready], actual: pending);
        Assert.DoesNotContain(expected: dimensionless, collection: pending);
    }

    /// <summary>Creates the store over a freshly-migrated database at <see cref="_dbPath"/>.</summary>
    private SqliteTaxonomyComparisonStore CreateStore()
    {
        return new SqliteTaxonomyComparisonStore(database: CreateDatabase(),
            options: Options.Create(new TranscriptOptions { Enabled = true }));
    }

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
            record: new TranscriptRecord(
                0,
                CorrelationId: correlationId,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                RequestedModel: "auto",
                RoutedModel: "model-a",
                Dimension: dimension,
                null,
                null,
                false,
                PromptText: "p",
                ResponseText: "r",
                null,
                0.01m,
                false,
                1.0,
                10,
                5,
                null,
                DimBestModel: "model-b"),
            cancellationToken: token);
        await store.LinkMemoryEntryAsync(transcriptId: id!.Value, 1, cancellationToken: token);
        await store.UpdateOutcomeAsync(correlationId: correlationId, 0.8, cancellationToken: token);
        return id.Value;
    }

    /// <summary>A minimal but fully-populated comparison row for round-trip assertions.</summary>
    private static TaxonomyComparisonRecord MakeRecord(long transcriptId)
    {
        return new TaxonomyComparisonRecord(
            TranscriptId: transcriptId,
            ComparedAtUtc: DateTimeOffset.UtcNow,
            SessionId: "session-1",
            0.8,
            0.7,
            null,
            0.1,
            null,
            false,
            false,
            RoutedModel: "model-a",
            BaselineModel: "model-b",
            0.01m,
            0.03m,
            0.02m,
            null,
            null);
    }

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
}