using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Owns the SQLite connection string and schema for both of the router's learned-memory tables: the
/// embedding-keyed <see cref="MemoryEntry"/> working set (<c>memory_entries</c>, PLAN.md Phase J) and the
/// dimension-keyed score aggregates behind <see cref="RouterMemory"/> (<c>dimension_scores</c>). A dedicated
/// file, separate from <see cref="TotallyHot.ArcRouter.PriceCatalog.PriceCatalogDatabase"/>'s
/// <c>agent_telemetry.db</c> - router memory has its own lifecycle and locking needs, independent of
/// price-catalog refreshes.
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
        """;

    /// <summary>The resolved absolute path of the database file.</summary>
    private readonly string _databasePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterMemoryDatabase"/> class.
    /// </summary>
    /// <param name="routingOptions">The routing options containing the database path.</param>
    public RouterMemoryDatabase(IOptions<RoutingOptions> routingOptions)
    {
        ArgumentNullException.ThrowIfNull(routingOptions);

        var configuredPath = routingOptions.Value.EmbeddingMemoryDatabasePath;
        _databasePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    /// <summary>Gets the resolved absolute path of the database file.</summary>
    public string DatabasePath => _databasePath;

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

        using var schema = connection.CreateCommand();
        schema.CommandText = SchemaSql;
        schema.ExecuteNonQuery();

        MigrateProvenanceColumns(connection);
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
        if (!ColumnExists(connection, "memory_entries", "is_exploratory"))
        {
            using var alterIsExploratory = connection.CreateCommand();
            alterIsExploratory.CommandText = "ALTER TABLE memory_entries ADD COLUMN is_exploratory INTEGER NOT NULL DEFAULT 0;";
            alterIsExploratory.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, "memory_entries", "propensity"))
        {
            using var alterPropensity = connection.CreateCommand();
            alterPropensity.CommandText = "ALTER TABLE memory_entries ADD COLUMN propensity REAL NOT NULL DEFAULT 1.0;";
            alterPropensity.ExecuteNonQuery();
        }
    }

    /// <summary>Checks whether <paramref name="table"/> already has a column named <paramref name="column"/>.</summary>
    /// <param name="connection">An open connection to query.</param>
    /// <param name="table">The table name. Must be a compile-time constant - interpolated directly into the PRAGMA, which does not accept a bound parameter for a table name.</param>
    /// <param name="column">The column name to look for.</param>
    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }
}
