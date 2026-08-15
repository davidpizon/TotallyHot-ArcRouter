using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// Owns the SQLite connection string and schema for the CodeRouterBench corpus
/// (docs/router/coderouterbench-sqlite-migration-plan.md). A dedicated <c>coderouterbench.db</c> file,
/// separate from <see cref="PriceCatalogDatabase"/>'s <c>agent_telemetry.db</c> and
/// <see cref="TotallyHot.ArcRouter.Router.RouterMemoryDatabase"/>'s file: the corpus is written only during explicit sync,
/// bulk (~91k result rows), and freely re-downloadable from Hugging Face, so a sync (delete-and-replace) never contends for the
/// price catalog's or router memory's WAL writer lock, and the corpus never bloats a backup of either
/// operational database.
/// </summary>
public sealed class BenchmarkDatabase
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS benchmark_files (
            file_name        TEXT    PRIMARY KEY,
            published_oid    TEXT    NOT NULL,
            size_bytes       INTEGER NOT NULL,
            row_count        INTEGER NOT NULL,
            repo_commit      TEXT    NOT NULL,
            synced_at_utc    TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS benchmark_id_results (
            task_id          TEXT    NOT NULL,
            split            TEXT    NOT NULL,
            source_split     TEXT    NOT NULL,
            dimension        TEXT    NOT NULL,
            model            TEXT    NOT NULL,
            score            REAL    NOT NULL,
            cost_usd         REAL    NULL,
            input_tokens     INTEGER NULL,
            output_tokens    INTEGER NULL,
            total_tokens     INTEGER NULL,
            latency_ms       INTEGER NULL,
            cost_source      TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS idx_benchmark_id_results_dimension_model
            ON benchmark_id_results (dimension, model);
        CREATE INDEX IF NOT EXISTS idx_benchmark_id_results_split
            ON benchmark_id_results (split);
        CREATE INDEX IF NOT EXISTS idx_benchmark_id_results_task_id
            ON benchmark_id_results (task_id);

        CREATE TABLE IF NOT EXISTS benchmark_ood_results (
            task_id          TEXT    NOT NULL,
            source_split     TEXT    NOT NULL,
            bench            TEXT    NOT NULL,
            original_task_id TEXT    NULL,
            dimension        TEXT    NOT NULL,
            model            TEXT    NOT NULL,
            source_model     TEXT    NULL,
            resolved         INTEGER NULL,
            apply_ok         INTEGER NULL,
            graded           INTEGER NULL,
            in_tok           INTEGER NULL,
            out_tok          INTEGER NULL,
            calls            INTEGER NULL,
            cost_usd         REAL    NULL,
            source_status    TEXT    NULL,
            cost_source      TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS idx_benchmark_ood_results_task_id
            ON benchmark_ood_results (task_id);

        CREATE TABLE IF NOT EXISTS benchmark_id_tasks (
            task_id          TEXT    PRIMARY KEY,
            split            TEXT    NOT NULL,
            source_split     TEXT    NOT NULL,
            dimension        TEXT    NOT NULL,
            raw_json         TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS benchmark_ood_tasks (
            task_id          TEXT    PRIMARY KEY,
            source_split     TEXT    NOT NULL,
            bench            TEXT    NOT NULL,
            dimension        TEXT    NOT NULL,
            language         TEXT    NULL,
            difficulty       TEXT    NULL,
            raw_json         TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS benchmark_models (
            model            TEXT    PRIMARY KEY,
            canonical_key    TEXT    NOT NULL,
            provider         TEXT    NULL,
            tier             TEXT    NULL,
            input_per_1m     REAL    NULL,
            output_per_1m    REAL    NULL,
            raw_json         TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS benchmark_summary (
            key              TEXT    PRIMARY KEY,
            raw_json         TEXT    NOT NULL
        );
        """;

    private readonly string _databasePath;
    private readonly ILogger<BenchmarkDatabase>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BenchmarkDatabase"/> class.
    /// </summary>
    /// <param name="storageOptions">The shared storage options containing the benchmark database path.</param>
    /// <param name="logger">
    /// Logs a summary when <see cref="EnsureCreated"/> repairs rows written by either of the two now-fixed
    /// importer defects (docs/router/live-feedback-learning-plan.md Phase 1). Optional so existing direct
    /// construction (e.g. test helpers) keeps compiling; repairs still run silently without it.
    /// </param>
    public BenchmarkDatabase(IOptions<StorageOptions> storageOptions, ILogger<BenchmarkDatabase>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        _databasePath = storageOptions.Value.ResolveBenchmarkDatabasePath();
        _logger = logger;
    }

    /// <summary>Gets the resolved absolute path of the database file.</summary>
    public string DatabasePath => _databasePath;

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    /// <summary>
    /// Opens a connection to the database. The caller owns disposal.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Ensures the database file, its directory, and all tables exist. Idempotent: a second call on an
    /// existing file changes nothing.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the file already existed before this call; <see langword="false"/> if it
    /// was just created.
    /// </returns>
    public bool EnsureCreated()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var alreadyExisted = File.Exists(_databasePath);

        using var connection = OpenConnection();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = SchemaSql;
            schema.ExecuteNonQuery();
        }

        RepairModelsEnvelopeRow(connection);
        RepairIdTasksSourceSplit(connection);

        return alreadyExisted;
    }

    // Repairs docs/router/live-feedback-learning-plan.md Phase 1a in place: a database imported before
    // BenchmarkModelsJsonImporter unwrapped the {"models": [...]} envelope has exactly one
    // benchmark_models row, keyed 'models', whose raw_json is the entire published models array. Both the
    // bad state and its fix are re-derivable from that one row's raw_json - no re-download needed. A
    // correctly-imported database has no row keyed 'models' (a real model id never collides with the
    // envelope's own wrapper key - see BenchmarkModelsJsonImporterTests'
    // Import_ObjectShapeWithLiteralModelsKeyAmongOthers test for why that distinction is safe), so this is
    // a no-op there.
    private void RepairModelsEnvelopeRow(SqliteConnection connection)
    {
        string? rawJson;
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT raw_json FROM benchmark_models WHERE model = 'models';";
            rawJson = select.ExecuteScalar() as string;
        }

        if (rawJson is null)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        // BenchmarkModelsJsonImporter.Import replaces every existing benchmark_models row; in the broken
        // state described above, the garbage 'models' row is the only row in the table, so this is exactly
        // the delete-and-reimport the repair needs.
        var repairedCount = BenchmarkModelsJsonImporter.Import(rawJson, connection, transaction);
        transaction.Commit();

        _logger?.LogInformation(
            "Repaired the benchmark_models 'models' envelope defect: replaced 1 garbage row with {RepairedCount} model row(s).",
            repairedCount);
    }

    // Repairs docs/router/live-feedback-learning-plan.md Phase 1b in place: a database imported before
    // BenchmarkIdTasksJsonlImporter read "source_split" has that column holding the same
    // probing/id_test value as the split column, instead of upstream's train/val/test distinction. Both
    // columns are keyed the same way in the broken state, so any row whose source_split is not one of the
    // three valid values is repairable from its own raw_json - no re-download needed. Rows whose raw_json
    // never carried a source_split property (already covered by the importer's own split-argument
    // fallback) are left untouched, matching what a correct import would have produced for them.
    private void RepairIdTasksSourceSplit(SqliteConnection connection)
    {
        List<(string TaskId, string RawJson)> badRows = [];
        using (var select = connection.CreateCommand())
        {
            select.CommandText =
                "SELECT task_id, raw_json FROM benchmark_id_tasks WHERE source_split NOT IN ('train', 'val', 'test');";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                badRows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (badRows.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE benchmark_id_tasks SET source_split = $sourceSplit WHERE task_id = $taskId;";
        var sourceSplitParam = update.Parameters.Add("$sourceSplit", SqliteType.Text);
        var taskIdParam = update.Parameters.Add("$taskId", SqliteType.Text);

        var repairedCount = 0;
        foreach (var (taskId, rawJson) in badRows)
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!document.RootElement.TryGetProperty("source_split", out var sourceSplitElement) ||
                sourceSplitElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            sourceSplitParam.Value = sourceSplitElement.GetString()!;
            taskIdParam.Value = taskId;
            update.ExecuteNonQuery();
            repairedCount++;
        }

        transaction.Commit();

        if (repairedCount > 0)
        {
            _logger?.LogInformation(
                "Repaired the benchmark_id_tasks source_split defect: corrected {RepairedCount} of {CandidateCount} affected row(s) from raw_json.",
                repairedCount,
                badRows.Count);
        }
    }
}
