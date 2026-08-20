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
            memory_entry_id    INTEGER NULL
        );

        CREATE INDEX IF NOT EXISTS ix_request_transcripts_correlation_id
            ON request_transcripts (correlation_id);
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

        using var schema = connection.CreateCommand();
        schema.CommandText = SchemaSql;
        schema.ExecuteNonQuery();
    }
}
