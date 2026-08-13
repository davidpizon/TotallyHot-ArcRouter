using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="BenchmarkDatabase"/> class.
    /// </summary>
    /// <param name="storageOptions">The shared storage options containing the benchmark database path.</param>
    public BenchmarkDatabase(IOptions<StorageOptions> storageOptions)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        _databasePath = storageOptions.Value.ResolveBenchmarkDatabasePath();
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

        return alreadyExisted;
    }
}
