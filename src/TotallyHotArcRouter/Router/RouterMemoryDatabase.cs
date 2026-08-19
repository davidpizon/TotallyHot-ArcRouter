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
    }
}
