using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Owns the SQLite connection string and schema for the opt-in transcript store
/// (docs/router/self-organizing-classification-plan.md Phase T1a). A dedicated file
/// (<c>transcripts.db</c>, resolved via <see cref="StorageOptions.ResolveTranscriptDatabasePath"/>),
/// separate from <see cref="Router.RouterMemoryDatabase"/>'s file: this table carries raw prompt/response
/// text, which the router's other learned-memory tables deliberately do not, so its creation is gated on
/// <see cref="TranscriptOptions.Enabled"/> rather than happening unconditionally at startup like every
/// other database in this codebase.
/// </summary>
public sealed class TranscriptDatabase
{
    /// <summary>DDL creating the <c>request_transcripts</c> table if it does not already exist.</summary>
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS request_transcripts (
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
            dim_best_model     TEXT    NULL,
            scorer_version     TEXT    NULL
        );

        CREATE INDEX IF NOT EXISTS ix_request_transcripts_correlation_id
            ON request_transcripts (correlation_id);

        CREATE TABLE IF NOT EXISTS taxonomy_comparisons (
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
            estimated_net_savings_usd REAL    NULL,
            baseline_predicted_score  REAL    NULL,
            estimated_regret          REAL    NULL
        );

        CREATE INDEX IF NOT EXISTS ix_taxonomy_comparisons_session
            ON taxonomy_comparisons (session_id);

        CREATE INDEX IF NOT EXISTS ix_taxonomy_comparisons_compared_at
            ON taxonomy_comparisons (compared_at_utc);
        """;

    /// <summary>The resolved absolute path of the database file.</summary>
    private readonly string _databasePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscriptDatabase"/> class.
    /// </summary>
    /// <param name="storageOptions">Supplies the database path via <see cref="StorageOptions.ResolveTranscriptDatabasePath"/>.</param>
    public TranscriptDatabase(IOptions<StorageOptions> storageOptions)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        _databasePath = storageOptions.Value.ResolveTranscriptDatabasePath();
    }

    /// <summary>Gets the resolved absolute path of the database file.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>Gets the SQLite connection string for <see cref="DatabasePath"/>.</summary>
    private string ConnectionString => $"Data Source={_databasePath}";

    /// <summary>
    /// Opens a connection to the database. The caller owns disposal. Does not itself call
    /// <see cref="EnsureCreated"/> - callers gate that on <see cref="TranscriptOptions.Enabled"/> first.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Ensures the database file, its directory, and the <c>request_transcripts</c> table exist.
    /// Idempotent: a second call on an existing file changes nothing. Callers must only invoke this when
    /// <see cref="TranscriptOptions.Enabled"/> is <see langword="true"/> - with capture disabled, no table
    /// is created and nothing is written, per the plan's exit criterion.
    /// </summary>
    public void EnsureCreated()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

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

        MigrateDimBestModelColumn(connection);
        MigrateTaxonomyComparisonRegretColumns(connection);
        MigrateScorerVersionColumn(connection);
        MigrateSessionIdColumn(connection);
    }

    /// <summary>
    /// Adds the <c>session_id</c> column to <c>request_transcripts</c> if it is missing, plus its index,
    /// and backfills every existing row by parsing <c>correlation_id</c>
    /// (<see cref="CorrelationIdParser.SessionIdOf"/>) - docs/router/sessions-tab-training-data-plan.md
    /// Phase 1. Lets the GUI's Sessions tab group transcript rows into sessions without re-parsing
    /// <c>correlation_id</c> on every read.
    /// </summary>
    /// <param name="connection">An open connection to the transcript database.</param>
    /// <remarks>
    /// Same additive <c>PRAGMA</c>-check convention as <see cref="MigrateDimBestModelColumn"/>. Unlike
    /// that migration, this one backfills: <c>session_id</c> is derivable from <c>correlation_id</c> for
    /// every row that has ever been written (<c>ProxyMiddleware</c> has composed correlation ids as
    /// <c>"{sessionId}:{turnNumber}"</c> since before this table existed), so leaving existing rows at the
    /// column's <c>NOT NULL DEFAULT ''</c> would make every pre-migration session invisible to session
    /// grouping instead of just newly written ones. The backfill runs in C# rather than a single SQL
    /// statement so it uses the exact same parser new writes do, rather than a second, SQL-only
    /// reimplementation of the same rule that could drift from it.
    /// </remarks>
    private static void MigrateSessionIdColumn(SqliteConnection connection)
    {
        bool columnJustAdded;
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('request_transcripts') WHERE name = 'session_id';";
            columnJustAdded = Convert.ToInt64(pragma.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0;
        }

        if (columnJustAdded)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE request_transcripts ADD COLUMN session_id TEXT NOT NULL DEFAULT '';";
            alter.ExecuteNonQuery();

            BackfillSessionIdColumn(connection);
        }

        using var index = connection.CreateCommand();
        index.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_request_transcripts_session_id
                ON request_transcripts (session_id);
            """;
        index.ExecuteNonQuery();
    }

    /// <summary>
    /// Fills in <c>session_id</c> for every row left at the column's default empty string by
    /// <see cref="MigrateSessionIdColumn"/>'s <c>ALTER TABLE</c>, parsed from each row's own
    /// <c>correlation_id</c> via <see cref="CorrelationIdParser.SessionIdOf"/>.
    /// </summary>
    /// <param name="connection">An open connection to the transcript database, mid-migration.</param>
    private static void BackfillSessionIdColumn(SqliteConnection connection)
    {
        List<(long Id, string CorrelationId)> rows = [];
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id, correlation_id FROM request_transcripts WHERE session_id = '';";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE request_transcripts SET session_id = $sessionId WHERE id = $id;";
        var sessionIdParam = update.Parameters.Add("$sessionId", SqliteType.Text);
        var idParam = update.Parameters.Add("$id", SqliteType.Integer);

        foreach (var (id, correlationId) in rows)
        {
            sessionIdParam.Value = CorrelationIdParser.SessionIdOf(correlationId);
            idParam.Value = id;
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Adds the <c>scorer_version</c> column to <c>request_transcripts</c> if it is missing, plus the index
    /// <see cref="SqliteTranscriptStore"/>'s rescan sweep selects on, so a database created by a
    /// pre-rescan build picks both up on startup instead of failing every sweep with "no such column".
    /// </summary>
    /// <param name="connection">An open connection to the transcript database.</param>
    /// <remarks>
    /// Same additive <c>PRAGMA</c>-check convention as <see cref="MigrateDimBestModelColumn"/>. The index
    /// is created here rather than in <see cref="SchemaSql"/> because on an existing database the schema
    /// statement runs <em>before</em> this migration, so indexing the column there would reference one that
    /// does not exist yet.
    /// <para>
    /// The column is nullable with no backfill, and null carries a specific meaning the rescan depends on:
    /// "this row has never been graded by a versioned scorer." Backfilling it to the current version would
    /// mark rows as already-scanned that were graded by an older scorer, or never graded at all, silently
    /// excluding exactly the rows the sweep exists to pick up.
    /// </para>
    /// </remarks>
    private static void MigrateScorerVersionColumn(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('request_transcripts') WHERE name = 'scorer_version';";
            if (Convert.ToInt64(pragma.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE request_transcripts ADD COLUMN scorer_version TEXT NULL;";
                alter.ExecuteNonQuery();
            }
        }

        using var index = connection.CreateCommand();
        index.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_request_transcripts_scorer_version
                ON request_transcripts (scorer_version);
            """;
        index.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds the <c>dim_best_model</c> column to <c>request_transcripts</c> if it is missing, so a
    /// transcript database created by a Phase T1 build picks it up on startup instead of failing every
    /// read with "no such column".
    /// </summary>
    /// <param name="connection">An open connection to the transcript database.</param>
    /// <remarks>
    /// <c>CREATE TABLE IF NOT EXISTS</c> is blind to a table that already exists with an older shape, and
    /// this repo has no migration framework, so the check is an explicit <c>PRAGMA</c> - the same additive
    /// convention <c>PriceCatalogDatabase.MigrateEnabledColumn</c> established. The column is nullable with
    /// no backfill because none is possible: rows captured before Phase T4 never recorded what
    /// <c>dim_best</c> would have picked, and inventing a value for them would fabricate exactly the
    /// counterfactual this phase exists to measure honestly.
    /// </remarks>
    private static void MigrateDimBestModelColumn(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('request_transcripts') WHERE name = 'dim_best_model';";
            if (Convert.ToInt64(pragma.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE request_transcripts ADD COLUMN dim_best_model TEXT NULL;";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds the <c>baseline_predicted_score</c> and <c>estimated_regret</c> columns to
    /// <c>taxonomy_comparisons</c> if they are missing, so a database created by a pre-regret build
    /// (docs/router/routing-roi-regret-plan.md) picks them up on startup instead of failing every write
    /// with "no such column".
    /// </summary>
    /// <param name="connection">An open connection to the transcript database.</param>
    /// <remarks>
    /// Same additive <c>PRAGMA</c>-check convention as <see cref="MigrateDimBestModelColumn"/>. Both
    /// columns are nullable with no backfill: existing comparison rows were computed without a regret
    /// estimate, and recomputing one later would score against a ledger that has drifted since the row was
    /// compared - <see langword="null"/> honestly means "not estimated when this row was written". The two
    /// columns travel together, so one existence check covers both.
    /// </remarks>
    private static void MigrateTaxonomyComparisonRegretColumns(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('taxonomy_comparisons') WHERE name = 'estimated_regret';";
            if (Convert.ToInt64(pragma.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = """
            ALTER TABLE taxonomy_comparisons ADD COLUMN baseline_predicted_score REAL NULL;
            ALTER TABLE taxonomy_comparisons ADD COLUMN estimated_regret REAL NULL;
            """;
        alter.ExecuteNonQuery();
    }
}
