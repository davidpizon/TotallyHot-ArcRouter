using TotallyHot.ArcRouter.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.PriceCatalog;
using Serilog;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Owns the SQLite connection string and schema for the router's learned-memory and settings tables: the
/// embedding-keyed <see cref="MemoryEntry"/> working set (<c>memory_entries</c>, PLAN.md Phase J), the
/// dimension-keyed score aggregates behind <see cref="RouterMemory"/> (<c>dimension_scores</c>), and the
/// mutable settings key/value store behind <see cref="RouterSettingsStore"/> (<c>router_settings</c>,
/// docs/router/self-organizing-classification-plan.md Phase T6). A dedicated file, separate from
/// <see cref="TotallyHot.ArcRouter.PriceCatalog.PriceCatalogDatabase"/>'s <c>agent_telemetry.db</c> - router
/// memory has its own lifecycle and locking needs, independent of price-catalog refreshes.
/// </summary>
public sealed class RouterMemoryDatabase
{
    /// <summary>
    /// DDL creating the router-memory tables if they do not already exist.
    /// </summary>
    /// <remarks>
    /// <c>dimension_scores</c> stores one row per (dimension, model) pair holding a running
    /// <see cref="ScoreAggregate"/>, rather than one row per observation. <see cref="RouterMemory"/> reads
    /// only the mean, so the aggregate is sufficient, and it keeps this table's size bounded by the
    /// (dimension x model) vocabulary instead of growing with traffic forever. The composite primary key is
    /// what lets <see cref="SqliteRouterMemoryStore.RecordScoreAsync"/> fold a new score in with a single
    /// atomic upsert.
    /// </remarks>
    private const string SchemaSql = """
                                     CREATE TABLE IF NOT EXISTS memory_entries (
                                         id               INTEGER PRIMARY KEY AUTOINCREMENT,
                                         embedding        BLOB    NOT NULL,
                                         chosen_model     TEXT    NOT NULL,
                                         score            REAL    NOT NULL,
                                         cost             REAL    NULL,
                                         verifier_trace   TEXT    NULL,
                                         created_at_utc   TEXT    NOT NULL
                                     );

                                     CREATE TABLE IF NOT EXISTS dimension_scores (
                                         dimension        TEXT    NOT NULL,
                                         model            TEXT    NOT NULL,
                                         sum              REAL    NOT NULL,
                                         count            INTEGER NOT NULL,
                                         PRIMARY KEY (dimension, model)
                                     );

                                     CREATE TABLE IF NOT EXISTS router_settings (
                                         key              TEXT    PRIMARY KEY,
                                         value            TEXT    NOT NULL
                                     );

                                     CREATE TABLE IF NOT EXISTS judge_shadow_scores (
                                         id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                                         correlation_id        TEXT    NOT NULL,
                                         created_at_utc        TEXT    NOT NULL,
                                         dimension             TEXT    NOT NULL,
                                         model                 TEXT    NOT NULL,
                                         static_score          REAL    NOT NULL,
                                         judge_score           REAL    NOT NULL,
                                         judge_model           TEXT    NOT NULL,
                                         judge_prompt_version  TEXT    NOT NULL,
                                         judge_latency_ms      INTEGER NOT NULL,
                                         used_logprobs         INTEGER NOT NULL,
                                         syntax_authoritative  INTEGER NULL
                                     );

                                     CREATE INDEX IF NOT EXISTS ix_judge_shadow_scores_correlation_id
                                         ON judge_shadow_scores (correlation_id);

                                     CREATE INDEX IF NOT EXISTS ix_judge_shadow_scores_created_at
                                         ON judge_shadow_scores (created_at_utc);

                                     CREATE TABLE IF NOT EXISTS grader_scores (
                                         id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                                         correlation_id          TEXT    NOT NULL,
                                         created_at_utc          TEXT    NOT NULL,
                                         dimension               TEXT    NOT NULL,
                                         model                   TEXT    NOT NULL,
                                         grader_key              TEXT    NOT NULL,
                                         score                   REAL    NOT NULL,
                                         grader_backbone_model   TEXT    NULL,
                                         response_length_chars   INTEGER NULL
                                     );

                                     CREATE INDEX IF NOT EXISTS ix_grader_scores_correlation_id
                                         ON grader_scores (correlation_id);

                                     CREATE INDEX IF NOT EXISTS ix_grader_scores_dimension_grader
                                         ON grader_scores (dimension, grader_key);

                                     CREATE INDEX IF NOT EXISTS ix_grader_scores_created_at
                                         ON grader_scores (created_at_utc);

                                     CREATE TABLE IF NOT EXISTS grade_receipts (
                                         correlation_id       TEXT    PRIMARY KEY,
                                         created_at_utc       TEXT    NOT NULL,
                                         dimension            TEXT    NOT NULL,
                                         model                TEXT    NOT NULL,
                                         grade_status         TEXT    NOT NULL,
                                         unified_score        REAL    NOT NULL,
                                         question_hmac        TEXT    NULL,
                                         question_chars       INTEGER NULL,
                                         context_messages     INTEGER NULL,
                                         question_truncated   INTEGER NOT NULL DEFAULT 0,
                                         fingerprint_ref      TEXT    NULL,
                                         scorer_version       TEXT    NOT NULL,
                                         prompt_version       TEXT    NULL,
                                         weights_version      TEXT    NOT NULL,
                                         abstained_graders    TEXT    NOT NULL DEFAULT '',
                                         incomplete_reason    TEXT    NULL,
                                         grade_source         TEXT    NULL,
                                         response_has_code    INTEGER NOT NULL DEFAULT 0,
                                         judge_model          TEXT    NULL,
                                         answer_model         TEXT    NULL,
                                         question_vector_missing INTEGER NULL,
                                         session_id           TEXT    NULL,
                                         user_signal          TEXT    NULL,
                                         spot_check           INTEGER NOT NULL DEFAULT 0,
                                         response_length_chars INTEGER NULL
                                     );

                                     CREATE TABLE IF NOT EXISTS response_vectors (
                                         correlation_id       TEXT    PRIMARY KEY,
                                         created_at_utc       TEXT    NOT NULL,
                                         embedding_model      TEXT    NOT NULL,
                                         vector               BLOB    NOT NULL
                                     );

                                     CREATE INDEX IF NOT EXISTS ix_response_vectors_created_at
                                         ON response_vectors (created_at_utc);

                                     CREATE INDEX IF NOT EXISTS ix_grade_receipts_created_at
                                         ON grade_receipts (created_at_utc);

                                     CREATE TABLE IF NOT EXISTS schema_meta (
                                         key                  TEXT    PRIMARY KEY,
                                         value                TEXT    NOT NULL
                                     );
                                     """;

    /// <summary>
    /// The file name <see cref="RoutingOptions.EmbeddingMemoryDatabasePath"/> defaults to, used by
    /// <see cref="AdoptLegacyInstallDirectoryCopy"/> to recognize the default destination and to find the
    /// legacy copy under the install directory.
    /// </summary>
    private const string DefaultFileName = "router_embedding_memory.db";

    /// <summary>The resolved absolute path of the database file.</summary>
    private readonly string _databasePath;

    /// <summary>
    /// Absolute path of <c>transcripts.db</c> for the issue #319 link clear, or <see langword="null"/> when
    /// this instance was built from routing options alone. A null path skips that update.
    /// </summary>
    private readonly string? _transcriptDatabasePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterMemoryDatabase"/> class from routing options alone.
    /// The issue #319 purge then skips the transcript-link update, still deletes untrusted memory rows, and
    /// still writes its marker. Production uses <see cref="RouterMemoryDatabase(IOptions{RoutingOptions}, IOptions{StorageOptions})"/>.
    /// </summary>
    /// <remarks>
    /// A relative <see cref="RoutingOptions.EmbeddingMemoryDatabasePath"/> resolves against the
    /// machine-shared data directory (<see cref="AppDataPaths"/>), the same location every other database
    /// this application writes lives in. It previously resolved against
    /// <see cref="AppContext.BaseDirectory"/> - the <em>install</em> directory - which was wrong three ways
    /// and is why this changed:
    /// <list type="number">
    /// <item>
    /// Only an administrator could run the router. <c>%ProgramFiles%</c> is not writable by an ordinary
    /// account, so a developer or operator launching the exe directly got
    /// <c>SQLite Error 14: 'unable to open database file'</c> out of <see cref="EnsureCreated"/> before the
    /// host finished starting - while the installed <c>LocalSystem</c> service, which can write there,
    /// worked fine and hid the problem.
    /// </item>
    /// <item>
    /// The file sat in a directory the MSI owns and re-lays on every upgrade
    /// (<c>docs/router/packaging-and-distribution.md</c>), so the router's learned memory was one
    /// reinstall away from being orphaned or removed - the exact hazard
    /// <see cref="PriceCatalog.StorageOptions"/>' remarks moved every other data file out of that directory
    /// to avoid.
    /// </item>
    /// <item>
    /// It contradicted ADR-0014's machine-wide state model, under which the router's data lives in one
    /// machine-shared directory rather than partly beside the binaries.
    /// </item>
    /// </list>
    /// An existing database in the old location is adopted once by
    /// <see cref="PriceCatalog.LegacyStorageMigration"/> rather than abandoned. An absolute configured path
    /// is still honoured exactly as before.
    /// </remarks>
    /// <param name="routingOptions">The routing options containing the database path.</param>
    public RouterMemoryDatabase(IOptions<RoutingOptions> routingOptions)
        : this(routingOptions, transcriptDatabasePath: null)
    {
    }

    /// <summary>
    /// Initializes a new instance that can clear transcript links during the issue #319 purge.
    /// </summary>
    /// <param name="routingOptions">The routing options containing the embedding-memory database path.</param>
    /// <param name="storageOptions">
    /// Supplies <see cref="StorageOptions.ResolveTranscriptDatabasePath"/>. Production passes this from DI.
    /// The new purge test passes options whose path is a copy, so the machine-default transcript file is
    /// never opened.
    /// </param>
    public RouterMemoryDatabase(IOptions<RoutingOptions> routingOptions, IOptions<StorageOptions> storageOptions)
        : this(routingOptions, RequireTranscriptPath(storageOptions))
    {
    }

    private RouterMemoryDatabase(IOptions<RoutingOptions> routingOptions, string? transcriptDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(routingOptions);

        var configuredPath = routingOptions.Value.EmbeddingMemoryDatabasePath;
        _databasePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(path1: AppDataPaths.ResolveMachineSharedDirectory(), path2: configuredPath);
        _transcriptDatabasePath = transcriptDatabasePath;
    }

    private static string RequireTranscriptPath(IOptions<StorageOptions> storageOptions)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(storageOptions.Value);
        return storageOptions.Value.ResolveTranscriptDatabasePath();
    }

    /// <summary>Gets the resolved absolute path of the database file.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Adopts a database left in the install directory by a build that resolved a relative
    /// <see cref="RoutingOptions.EmbeddingMemoryDatabasePath"/> against
    /// <see cref="AppContext.BaseDirectory"/>, so an upgrading install keeps its learned memory instead of
    /// silently starting empty. Does nothing when there is nothing to adopt, which is every run after the
    /// first and every fresh install.
    /// </summary>
    /// <remarks>
    /// This lives here rather than in <see cref="PriceCatalog.LegacyStorageMigration"/>, where the five
    /// other relocated files are handled, purely because of ordering:
    /// <see cref="EnsureCreated"/> is reached from <see cref="RouterSettingsStore"/>'s constructor while
    /// the DI container is still being built, which is strictly before that migration's hosted service
    /// starts. A migration placed there would therefore always arrive to find the empty database this very
    /// method had already created, and decline to overwrite it - which is exactly what happened when it was
    /// first written there, and is why it moved.
    /// <para>
    /// Deliberately conservative in three ways. It only adopts when the destination does not exist, so it
    /// can never overwrite real memory. It only acts when the configured path resolved to the
    /// machine-shared default, so an operator who pinned their own location is never silently seeded from a
    /// file they may have abandoned. And every failure is swallowed after logging: the memory is relearned
    /// from ordinary traffic, so failing to adopt it must degrade to "start fresh" rather than take the host
    /// down - which, before the path itself was corrected, is precisely how an unwritable install directory
    /// killed startup.
    /// </para>
    /// </remarks>
    private void AdoptLegacyInstallDirectoryCopy()
    {
        if (File.Exists(_databasePath)) return;

        // Only ever adopt into the default location - see the remarks.
        var defaultPath = Path.Combine(path1: AppDataPaths.ResolveMachineSharedDirectory(),
            path2: DefaultFileName);
        if (!string.Equals(a: _databasePath, b: defaultPath, comparisonType: StringComparison.OrdinalIgnoreCase))
            return;

        var legacyPath = Path.Combine(path1: AppContext.BaseDirectory, path2: DefaultFileName);
        if (!File.Exists(legacyPath)) return;

        try
        {
            // VACUUM INTO, not File.Copy: this database runs in WAL mode (see EnsureCreated's pragmas), so
            // its committed state is spread across .db/-wal/-shm and copying only the first would silently
            // drop everything not yet checkpointed.
            LegacyStorageMigration.CopyDatabase(legacyPath: legacyPath, destinationPath: _databasePath);

            // Microsoft.Data.Sqlite pools connections, so disposing the one CopyDatabase opened does not
            // close the OS handle on the legacy file - it goes back to the pool still holding it, and the
            // rename below then fails with "used by another process" even though the copy succeeded. Clearing
            // the pool releases it. Without this the adoption still works but logs a spurious warning every
            // time, which is exactly how this was found.
            SqliteConnection.ClearAllPools();

            // Rename rather than delete, matching LegacyStorageMigration: the operator keeps a recoverable
            // copy, and the suffix stops a later run from adopting it a second time. A failure here is
            // logged but not fatal - the adoption itself already succeeded, and the destination check above
            // is what actually prevents a repeat.
            try
            {
                File.Move(sourceFileName: legacyPath,
                    destFileName: legacyPath + LegacyStorageMigration.MigratedSuffix, true);
            }
            catch (Exception renameFailure) when (renameFailure is IOException or UnauthorizedAccessException)
            {
                Log.Warning(exception: renameFailure,
                    messageTemplate:
                    "Adopted the router memory database from {LegacyPath} but could not rename it aside; it is left in place and will be ignored from now on.",
                    propertyValue: legacyPath);
            }

            Log.Information(
                messageTemplate:
                "Adopted the router memory database from the legacy install-directory location {LegacyPath} into {DatabasePath}.",
                propertyValue0: legacyPath, propertyValue1: _databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            Log.Warning(exception: ex,
                messageTemplate:
                "Could not adopt the router memory database from {LegacyPath}; continuing with a new, empty one.",
                propertyValue: legacyPath);

            TryDeletePartialDestination();
        }
    }

    /// <summary>
    /// Removes a destination file a failed adoption may have half-written, so the fresh database
    /// <see cref="EnsureCreated"/> goes on to create is not built on a truncated copy.
    /// </summary>
    private void TryDeletePartialDestination()
    {
        try
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(exception: ex,
                messageTemplate: "Could not remove the partially adopted router memory database at {DatabasePath}.",
                propertyValue: _databasePath);
        }
    }

    /// <summary>Gets the SQLite connection string for <see cref="_databasePath"/>.</summary>
    private string ConnectionString => $"Data Source={_databasePath}";

    /// <summary>
    /// Opens a connection to the database. The caller owns disposal.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        return SqliteHardening.Open(ConnectionString);
    }

    /// <summary>
    /// Ensures the database file, its directory, and the <c>memory_entries</c> table exist. Idempotent:
    /// a second call on an existing file changes nothing.
    /// </summary>
    public void EnsureCreated()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        AdoptLegacyInstallDirectoryCopy();

        using var connection = OpenConnection();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        // Finish any eviction a previous run left pending (#184): nothing else has this database open yet.
        // A reader can still hold the log (a background service that started earlier, or another connection),
        // so a busy result is reported rather than assumed away. The next eviction (SqliteMemoryEntryStore
        // truncates after every delete) or the next start truncates it again.
        if (!SqliteHardening.TruncateWal(connection))
            Log.Warning(
                "The router memory database's write-ahead log was busy at startup; pending deletions will be finalized by the next eviction or restart.");

        using var schema = connection.CreateCommand();
        schema.CommandText = SchemaSql;
        schema.ExecuteNonQuery();

        MigrateProvenanceColumns(connection);
        MigrateDimensionColumn(connection);
        MigrateIsJudgeScoredColumn(connection);
        MigrateEmbeddingModelColumn(connection);
        MigrateJudgeShadowScoreColumns(connection);
        MigrateGraderScoreQuestionColumns(connection);
        MigrateGradeReceiptIssue304Columns(connection);
        MigrateMemoryEntryCorrelationIdColumn(connection);
        MigrateMemoryEntriesCostNullable(connection);
        MigrateIssue293LiveLearningPurge(connection);
        MigrateIssue319UntrustedJudgeProvenancePurge(connection);
    }

    /// <summary>
    /// Marker in <c>schema_meta</c> recording that pre-#293 live learning was purged (plan §6).
    /// </summary>
    internal const string Issue293LiveLearningPurgeKey = "issue-293-live-learning-purge";

    /// <summary>
    /// Marker in <c>schema_meta</c> recording that memory rows whose judge-score provenance could not be
    /// trusted were purged (issue #319).
    /// </summary>
    private const string Issue319UntrustedJudgeProvenancePurgeKey = "issue-319-untrusted-judge-provenance-purge";

    /// <summary>
    /// How many bound parameters one transcript <c>UPDATE</c> uses. Kept under SQLite's historical
    /// variable limit so a chunk commits on its own and a later retry can run the same statement again.
    /// </summary>
    private const int TranscriptLinkParameterChunkSize = 500;

    /// <summary>
    /// Default live-memory prefix used when purging <c>dimension_scores</c> rows; matches
    /// <see cref="Quality.QualityOptions.LiveMemoryPrefix"/>.
    /// </summary>
    private const string DefaultLiveMemoryPrefix = "live:";

    // CREATE TABLE IF NOT EXISTS silently does nothing when the table already exists, so it cannot add a
    // column to a database created before provenance tracking existed - the same blind spot
    // PriceCatalogDatabase.MigrateEnabledColumn documents and works around. Without this, every install
    // predating docs/router/self-organizing-classification-plan.md Phase T1c would fail on first read with
    // "no such column". Defaulting both columns to "not exploratory, certain selection" (0 / 1.0) is exactly
    // how every pre-existing row actually behaved: it was written before either concept existed, by a policy
    // with no exploration mechanism recorded against it.
    /// <summary>
    /// Adds the `is_exploratory` and `propensity` columns to `memory_entries` if missing, so databases
    /// created before docs/router/self-organizing-classification-plan.md Phase T1c pick them up on startup,
    /// with existing rows defaulting to non-exploratory, certain-propensity provenance.
    /// </summary>
    private static void MigrateProvenanceColumns(SqliteConnection connection)
    {
        if (!ColumnExists(connection: connection, table: "memory_entries", column: "is_exploratory"))
        {
            using var alterIsExploratory = connection.CreateCommand();
            alterIsExploratory.CommandText =
                "ALTER TABLE memory_entries ADD COLUMN is_exploratory INTEGER NOT NULL DEFAULT 0;";
            alterIsExploratory.ExecuteNonQuery();
        }

        if (!ColumnExists(connection: connection, table: "memory_entries", column: "propensity"))
        {
            using var alterPropensity = connection.CreateCommand();
            alterPropensity.CommandText = "ALTER TABLE memory_entries ADD COLUMN propensity REAL NOT NULL DEFAULT 1.0;";
            alterPropensity.ExecuteNonQuery();
        }
    }

    // Same blind spot MigrateProvenanceColumns works around, for a column added by
    // docs/router/self-organizing-classification-plan.md Phase T2. NULL default (not "unknown") because a
    // pre-existing row genuinely has no recorded dimension label - the clustering histogram (Phase T2e)
    // must be able to tell "never labeled" apart from a real dimension value.
    /// <summary>
    /// Adds the `dimension` column to `memory_entries` if missing, so databases created before Phase T2
    /// pick it up on startup, with existing rows defaulting to an unlabeled <see langword="null"/>.
    /// </summary>
    private static void MigrateDimensionColumn(SqliteConnection connection)
    {
        if (!ColumnExists(connection: connection, table: "memory_entries", column: "dimension"))
        {
            using var alterDimension = connection.CreateCommand();
            alterDimension.CommandText = "ALTER TABLE memory_entries ADD COLUMN dimension TEXT NULL;";
            alterDimension.ExecuteNonQuery();
        }
    }

    // Same blind spot MigrateProvenanceColumns/MigrateDimensionColumn work around, for a column added by
    // docs/router/geval-shadow-scoring-plan.md Phase G1e. Landed early (G1 always writes 0/false) so every
    // learning consumer can be written against the final schema once, rather than needing a second
    // migration when Phase G3 first sets it to 1 - the same "land the provenance bit early" precedent
    // IsExploratory already set (see MigrateProvenanceColumns's remarks).
    /// <summary>
    /// Adds the `is_judge_scored` column to `memory_entries` if missing, so databases created before Phase
    /// G1 pick it up on startup, with existing (and every G1/G2-era) row defaulting to 0 - execution- or
    /// heuristic-grounded, not judge-graded.
    /// </summary>
    private static void MigrateIsJudgeScoredColumn(SqliteConnection connection)
    {
        if (!ColumnExists(connection: connection, table: "memory_entries", column: "is_judge_scored"))
        {
            using var alterIsJudgeScored = connection.CreateCommand();
            alterIsJudgeScored.CommandText =
                "ALTER TABLE memory_entries ADD COLUMN is_judge_scored INTEGER NOT NULL DEFAULT 0;";
            alterIsJudgeScored.ExecuteNonQuery();
        }
    }

    // Same blind spot the three migrations above work around. NULL default - not the current model's
    // identity - because backfilling a concrete identity would be a fabrication: this code cannot know
    // which model produced a row written before the column existed. NULL is the honest "unrecorded", and
    // MemoryEntry.MatchesEmbeddingModel deliberately reads it as "assume current" so that upgrading does
    // not silently discard an existing installation's whole corpus. See that method's remarks for why the
    // optimistic reading is the correct one rather than merely the convenient one.
    /// <summary>
    /// Adds the `embedding_model` column to `memory_entries` if missing, so databases created before this
    /// provenance existed pick it up on startup, with existing rows left <see langword="null"/> - unrecorded
    /// rather than falsely attributed.
    /// </summary>
    private static void MigrateEmbeddingModelColumn(SqliteConnection connection)
    {
        if (!ColumnExists(connection: connection, table: "memory_entries", column: "embedding_model"))
        {
            using var alterEmbeddingModel = connection.CreateCommand();
            alterEmbeddingModel.CommandText = "ALTER TABLE memory_entries ADD COLUMN embedding_model TEXT NULL;";
            alterEmbeddingModel.ExecuteNonQuery();
        }
    }

    // The executing verifier that produced `verifier_score` and `executed` is gone: scores now come from
    // static analysis blended with the judge, and there is no execution to have been grounded in. A
    // database predating that change still has the old shape, and `executed INTEGER NOT NULL` has no
    // default, so the current INSERT - which no longer supplies it - would fail on every write.
    /// <summary>
    /// Migrates `judge_shadow_scores` from the executing verifier's shape to the current one: renames
    /// `verifier_score` to `static_score`, drops the `executed` column, and adds Phase G2's
    /// `syntax_authoritative` column.
    /// </summary>
    /// <remarks>
    /// Every statement is guarded on the column's actual presence, so this is idempotent and a no-op on a
    /// database created by the current DDL. The historical rows are kept rather than truncated: their
    /// score column still means "the non-judge grade for this request", which is exactly what
    /// `static_score` means now - only its provenance changed, and that provenance is recoverable from the
    /// row's timestamp. Dropping `executed` does lose the execution-grounded flag from those old rows;
    /// nothing can be grounded in execution any more, so retaining a column that must read false forever
    /// would preserve the shape of the fact while discarding its meaning.
    /// </remarks>
    private static void MigrateJudgeShadowScoreColumns(SqliteConnection connection)
    {
        if (ColumnExists(connection: connection, table: "judge_shadow_scores", column: "verifier_score"))
        {
            using var rename = connection.CreateCommand();
            rename.CommandText = "ALTER TABLE judge_shadow_scores RENAME COLUMN verifier_score TO static_score;";
            rename.ExecuteNonQuery();
        }

        if (ColumnExists(connection: connection, table: "judge_shadow_scores", column: "executed"))
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE judge_shadow_scores DROP COLUMN executed;";
            drop.ExecuteNonQuery();
        }

        if (!ColumnExists(connection: connection, table: "judge_shadow_scores", column: "syntax_authoritative"))
        {
            using var add = connection.CreateCommand();

            // Deliberately NULL for every pre-existing row rather than defaulted to 0 or 1. The column
            // says whether a real parser or a heuristic graded the row, and no answer is recoverable
            // after the fact - the language that decides it was never stored here. A default would make
            // every historical row claim an authority it may not have had, quietly corrupting exactly
            // the split G2 added the column for; NULL reports them as their own "unknown" cohort instead.
            add.CommandText = "ALTER TABLE judge_shadow_scores ADD COLUMN syntax_authoritative INTEGER NULL;";
            add.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds nullable <c>question_hmac</c> and <c>prompt_version</c> to <c>grader_scores</c> when missing
    /// (issue #293 plan §2.4).
    /// </summary>
    private static void MigrateGraderScoreQuestionColumns(SqliteConnection connection)
    {
        if (!ColumnExists(connection: connection, table: "grader_scores", column: "question_hmac"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE grader_scores ADD COLUMN question_hmac TEXT NULL;";
            alter.ExecuteNonQuery();
        }

        if (!ColumnExists(connection: connection, table: "grader_scores", column: "prompt_version"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE grader_scores ADD COLUMN prompt_version TEXT NULL;";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds issue #304 receipt columns when missing: <c>incomplete_reason</c>, <c>grade_source</c>,
    /// <c>response_has_code</c>, <c>judge_model</c>, <c>answer_model</c>. Guarded by
    /// <c>PRAGMA table_info</c> so an existing database keeps its rows.
    /// </summary>
    private static void MigrateGradeReceiptIssue304Columns(SqliteConnection connection)
    {
        AddNullableTextColumn(connection: connection, table: "grade_receipts", column: "incomplete_reason");
        AddNullableTextColumn(connection: connection, table: "grade_receipts", column: "grade_source");
        if (!ColumnExists(connection: connection, table: "grade_receipts", column: "response_has_code"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE grade_receipts ADD COLUMN response_has_code INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }

        AddNullableTextColumn(connection: connection, table: "grade_receipts", column: "judge_model");
        AddNullableTextColumn(connection: connection, table: "grade_receipts", column: "answer_model");
        if (!ColumnExists(connection: connection, table: "grade_receipts", column: "question_vector_missing"))
        {
            using var alter = connection.CreateCommand();
            // No DEFAULT: a 0 would mark receipts written before the peek existed as hits.
            alter.CommandText = "ALTER TABLE grade_receipts ADD COLUMN question_vector_missing INTEGER NULL;";
            alter.ExecuteNonQuery();
        }

        AddNullableTextColumn(connection: connection, table: "grade_receipts", column: "session_id");
        AddNullableTextColumn(connection: connection, table: "grade_receipts", column: "user_signal");
        if (!ColumnExists(connection: connection, table: "grade_receipts", column: "spot_check"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE grade_receipts ADD COLUMN spot_check INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }

        if (!ColumnExists(connection: connection, table: "grade_receipts", column: "response_length_chars"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE grade_receipts ADD COLUMN response_length_chars INTEGER NULL;";
            alter.ExecuteNonQuery();
        }

        using var index = connection.CreateCommand();
        index.CommandText = """
                            CREATE INDEX IF NOT EXISTS ix_grade_receipts_session_created
                                ON grade_receipts (session_id, created_at_utc);
                            CREATE INDEX IF NOT EXISTS ix_grade_receipts_question_hmac_created
                                ON grade_receipts (question_hmac, created_at_utc);
                            """;
        index.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds <c>memory_entries.correlation_id</c> so a dispute can delete one neighbor by that id
    /// (issue #304 phase 4). Null on rows written before the column existed.
    /// </summary>
    private static void MigrateMemoryEntryCorrelationIdColumn(SqliteConnection connection)
    {
        AddNullableTextColumn(connection: connection, table: "memory_entries", column: "correlation_id");
        using var index = connection.CreateCommand();
        index.CommandText = """
                            CREATE INDEX IF NOT EXISTS ix_memory_entries_correlation_id
                                ON memory_entries (correlation_id);
                            """;
        index.ExecuteNonQuery();
    }

    /// <summary>
    /// Rebuilds <c>memory_entries</c> so <c>cost</c> may be NULL (issue #304 phase 5). Idempotent: a
    /// second call is a no-op when the column already allows blanks. One transaction copies every row
    /// and its embedding blob, recreates the correlation index, and swaps the tables; a failure leaves
    /// the old table unchanged. Existing numeric zeros are copied as zeros — they are not guessed into
    /// nulls (#290's backfill).
    /// </summary>
    private static void MigrateMemoryEntriesCostNullable(SqliteConnection connection)
    {
        if (!ColumnIsNotNull(connection: connection, table: "memory_entries", column: "cost"))
            return;

        using var transaction = connection.BeginTransaction();
        try
        {
            using (var create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = """
                                     CREATE TABLE memory_entries_new (
                                         id               INTEGER PRIMARY KEY AUTOINCREMENT,
                                         embedding        BLOB    NOT NULL,
                                         chosen_model     TEXT    NOT NULL,
                                         score            REAL    NOT NULL,
                                         cost             REAL    NULL,
                                         verifier_trace   TEXT    NULL,
                                         created_at_utc   TEXT    NOT NULL,
                                         is_exploratory   INTEGER NOT NULL DEFAULT 0,
                                         propensity       REAL    NOT NULL DEFAULT 1.0,
                                         dimension        TEXT    NULL,
                                         is_judge_scored  INTEGER NOT NULL DEFAULT 0,
                                         embedding_model  TEXT    NULL,
                                         correlation_id   TEXT    NULL
                                     );
                                     """;
                create.ExecuteNonQuery();
            }

            using (var copy = connection.CreateCommand())
            {
                copy.Transaction = transaction;
                copy.CommandText = """
                                   INSERT INTO memory_entries_new (
                                       id, embedding, chosen_model, score, cost, verifier_trace, created_at_utc,
                                       is_exploratory, propensity, dimension, is_judge_scored, embedding_model,
                                       correlation_id)
                                   SELECT
                                       id, embedding, chosen_model, score, cost, verifier_trace, created_at_utc,
                                       COALESCE(is_exploratory, 0), COALESCE(propensity, 1.0), dimension,
                                       COALESCE(is_judge_scored, 0), embedding_model, correlation_id
                                   FROM memory_entries;
                                   """;
                copy.ExecuteNonQuery();
            }

            using (var drop = connection.CreateCommand())
            {
                drop.Transaction = transaction;
                drop.CommandText = "DROP TABLE memory_entries;";
                drop.ExecuteNonQuery();
            }

            using (var rename = connection.CreateCommand())
            {
                rename.Transaction = transaction;
                rename.CommandText = "ALTER TABLE memory_entries_new RENAME TO memory_entries;";
                rename.ExecuteNonQuery();
            }

            using (var index = connection.CreateCommand())
            {
                index.Transaction = transaction;
                index.CommandText = """
                                    CREATE INDEX IF NOT EXISTS ix_memory_entries_correlation_id
                                        ON memory_entries (correlation_id);
                                    """;
                index.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="column"/> on <paramref name="table"/> is declared <c>NOT NULL</c>
    /// (<c>PRAGMA table_info</c> <c>notnull</c> bit).
    /// </summary>
    private static bool ColumnIsNotNull(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"notnull\" FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue(parameterName: "$column", value: column);
        var scalar = command.ExecuteScalar();
        return scalar is not null && Convert.ToInt32(value: scalar, provider: CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Adds a nullable TEXT column when the table exists and the column does not.</summary>
    private static void AddNullableTextColumn(SqliteConnection connection, string table, string column)
    {
        if (!ColumnExists(connection: connection, table: table, column: column))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT NULL;";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// One-time purge of pre-#293 live learning: all <c>memory_entries</c>, <c>live:</c> tallies, and
    /// accumulated judge/grader score rows (plan §6, ADR-0024).
    /// </summary>
    private static void MigrateIssue293LiveLearningPurge(SqliteConnection connection)
    {
        if (SchemaMetaExists(connection: connection, key: Issue293LiveLearningPurgeKey)) return;

        var memoryDeleted = ExecuteCountDelete(connection: connection, sql: "DELETE FROM memory_entries;");
        var liveTalliesDeleted = ExecuteCountDelete(connection: connection,
            sql: "DELETE FROM dimension_scores WHERE dimension LIKE $prefix;",
            parameterName: "$prefix", parameterValue: DefaultLiveMemoryPrefix + "%");
        var graderScoresDeleted = ExecuteCountDelete(connection: connection, sql: "DELETE FROM grader_scores;");
        var shadowScoresDeleted = ExecuteCountDelete(connection: connection, sql: "DELETE FROM judge_shadow_scores;");

        WriteSchemaMeta(connection: connection, key: Issue293LiveLearningPurgeKey, value: DateTimeOffset.UtcNow.ToString("O"));

        if (!SqliteHardening.TruncateWal(connection))
            Log.Warning(
                "The router memory database write-ahead log was busy after the issue-293 live-learning purge; deleted content will be finalized on the next restart or eviction.");

        using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }

        Log.Information(
            messageTemplate:
            "Issue-293 live-learning purge complete: {MemoryEntriesDeleted} memory_entries and {LiveTalliesDeleted} live dimension_scores deleted.",
            propertyValue0: memoryDeleted,
            propertyValue1: liveTalliesDeleted);
        Log.Information(
            messageTemplate:
            "Issue-293 live-learning purge complete: {GraderScoresDeleted} grader_scores and {ShadowScoresDeleted} judge_shadow_scores deleted.",
            propertyValue0: graderScoresDeleted,
            propertyValue1: shadowScoresDeleted);
    }

    /// <summary>
    /// One-time purge of <c>memory_entries</c> whose <c>is_judge_scored = 1</c> cannot be trusted (issue #319),
    /// then clears <c>request_transcripts.memory_entry_id</c> values that pointed at those rows. Runs after
    /// <see cref="MigrateIssue293LiveLearningPurge"/> and does not edit that purge. The marker is written only
    /// after both steps succeed. A missing transcript path or a missing transcript file skips the link clear
    /// and still deletes and writes the marker. A thrown transcript update leaves the rows and the marker
    /// absent so the next startup retries. A busy write-ahead log is logged and does not fail startup.
    /// </summary>
    /// <param name="connection">An open connection to <c>router_embedding_memory.db</c>.</param>
    private void MigrateIssue319UntrustedJudgeProvenancePurge(SqliteConnection connection)
    {
        if (SchemaMetaExists(connection: connection, key: Issue319UntrustedJudgeProvenancePurgeKey)) return;

        var ids = ReadJudgeScoredEntryIds(connection);
        var linksCleared = 0;
        if (ids.Count > 0 && !string.IsNullOrWhiteSpace(_transcriptDatabasePath))
        {
            if (File.Exists(_transcriptDatabasePath))
            {
                try
                {
                    linksCleared = ClearTranscriptLinks(transcriptPath: _transcriptDatabasePath, ids: ids);
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                {
                    Log.Warning(
                        exception: ex,
                        messageTemplate:
                        "Issue-319 untrusted judge-provenance purge left memory rows in place because the transcript link update failed; it will retry on the next startup.");
                    return;
                }

                if (!SqliteHardening.TruncateWal($"Data Source={_transcriptDatabasePath}"))
                    Log.Warning(
                        "The transcript database write-ahead log was busy after the issue-319 transcript-link clear; cleared links will be finalized on the next restart.");
            }
        }

        var memoryDeleted = ExecuteCountDelete(
            connection: connection,
            sql: "DELETE FROM memory_entries WHERE is_judge_scored = 1;");
        WriteSchemaMeta(
            connection: connection,
            key: Issue319UntrustedJudgeProvenancePurgeKey,
            value: DateTimeOffset.UtcNow.ToString("O"));

        if (!SqliteHardening.TruncateWal(connection))
            Log.Warning(
                "The router memory database write-ahead log was busy after the issue-319 untrusted judge-provenance purge; deleted content will be finalized on the next restart or eviction.");

        using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }

        Log.Information(
            messageTemplate:
            "Issue-319 untrusted judge-provenance purge complete: {MemoryEntriesDeleted} memory_entries deleted and {TranscriptLinksCleared} transcript links cleared.",
            propertyValue0: memoryDeleted,
            propertyValue1: linksCleared);
    }

    /// <summary>Reads the ids of <c>memory_entries</c> rows stamped <c>is_judge_scored = 1</c>.</summary>
    /// <param name="connection">An open connection to <c>router_embedding_memory.db</c>.</param>
    /// <returns>The ids. Empty when none match.</returns>
    private static List<long> ReadJudgeScoredEntryIds(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM memory_entries WHERE is_judge_scored = 1;";
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// Sets <c>request_transcripts.memory_entry_id</c> to NULL for each id in <paramref name="ids"/>, in
    /// chunks that each commit on their own.
    /// </summary>
    /// <param name="transcriptPath">Absolute path of an existing <c>transcripts.db</c>.</param>
    /// <param name="ids">The memory-entry ids this purge is about to delete. Must be non-empty.</param>
    /// <returns>The number of transcript rows updated.</returns>
    private static int ClearTranscriptLinks(string transcriptPath, IReadOnlyList<long> ids)
    {
        var cleared = 0;
        using var connection = SqliteHardening.Open($"Data Source={transcriptPath}");
        for (var offset = 0; offset < ids.Count; offset += TranscriptLinkParameterChunkSize)
        {
            var count = Math.Min(val1: TranscriptLinkParameterChunkSize, val2: ids.Count - offset);
            using var command = connection.CreateCommand();
            var names = new string[count];
            for (var i = 0; i < count; i++)
            {
                names[i] = "$id" + i.ToString(CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(parameterName: names[i], value: ids[offset + i]);
            }

            command.CommandText =
                "UPDATE request_transcripts SET memory_entry_id = NULL WHERE memory_entry_id IN (" +
                string.Join(separator: ", ", values: names) + ");";
            cleared += command.ExecuteNonQuery();
        }

        return cleared;
    }

    private static bool SchemaMetaExists(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_meta WHERE key = $key;";
        command.Parameters.AddWithValue(parameterName: "$key", value: key);
        return Convert.ToInt32(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture) > 0;
    }

    private static void WriteSchemaMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO schema_meta (key, value) VALUES ($key, $value)
                              ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                              """;
        command.Parameters.AddWithValue(parameterName: "$key", value: key);
        command.Parameters.AddWithValue(parameterName: "$value", value: value);
        command.ExecuteNonQuery();
    }

    private static int ExecuteCountDelete(
        SqliteConnection connection,
        string sql,
        string? parameterName = null,
        string? parameterValue = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameterName is not null)
            command.Parameters.AddWithValue(parameterName: parameterName, value: parameterValue!);
        return command.ExecuteNonQuery();
    }

    /// <summary>Checks whether <paramref name="table"/> already has a column named <paramref name="column"/>.</summary>
    /// <param name="connection">An open connection to query.</param>
    /// <param name="table">
    /// The table name. Must be a compile-time constant - interpolated directly into the PRAGMA, which does
    /// not accept a bound parameter for a table name.
    /// </param>
    /// <param name="column">The column name to look for.</param>
    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue(parameterName: "$column", value: column);
        return Convert.ToInt32(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture) > 0;
    }
}