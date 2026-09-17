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
                                         cost             REAL    NOT NULL,
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
    /// Initializes a new instance of the <see cref="RouterMemoryDatabase"/> class.
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
    {
        ArgumentNullException.ThrowIfNull(routingOptions);

        var configuredPath = routingOptions.Value.EmbeddingMemoryDatabasePath;
        _databasePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(path1: AppDataPaths.ResolveMachineSharedDirectory(), path2: configuredPath);
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
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
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
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        using var schema = connection.CreateCommand();
        schema.CommandText = SchemaSql;
        schema.ExecuteNonQuery();

        MigrateProvenanceColumns(connection);
        MigrateDimensionColumn(connection);
        MigrateIsJudgeScoredColumn(connection);
        MigrateEmbeddingModelColumn(connection);
        MigrateJudgeShadowScoreColumns(connection);
    }

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