using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Storage;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// One session file as the index knows it: where it is, the wrapped key that opens it, and how far its
/// committed prefix extends.
/// </summary>
/// <param name="ArchiveSessionId">The session's cross-machine identity.</param>
/// <param name="ClientSessionId">The client's own session id, kept as metadata because it can repeat across machines.</param>
/// <param name="FileName">The random file name inside the session folder.</param>
/// <param name="WrappedKey">The session key wrapped under the master key.</param>
/// <param name="CommittedLength">The file length after the last committed turn.</param>
/// <param name="CommittedFrames">The number of frames in the committed prefix.</param>
/// <param name="TurnCount">How many turns have committed.</param>
/// <param name="CreatedAtUtc">When the file was created.</param>
/// <param name="LastTurnAtUtc">The newest committed turn's timestamp, or <see langword="null"/> before the first turn.</param>
/// <param name="SessionSha256">
/// The session's checksum over its exchange bodies (see <see cref="SessionIndex.CommitTurn"/>), or
/// <see langword="null"/> when a turn has no body hashes yet (a session captured before they existed, until a
/// backfill runs).
/// </param>
public sealed record SessionFileRow(
    Guid ArchiveSessionId,
    string ClientSessionId,
    string FileName,
    byte[] WrappedKey,
    long CommittedLength,
    long CommittedFrames,
    int TurnCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastTurnAtUtc,
    byte[]? SessionSha256 = null);

/// <summary>
/// The hash of one stored body: what an export verifies against and what the session checksum is built from.
/// Holds no text.
/// </summary>
/// <param name="ArchiveTurnId">The turn the body belongs to.</param>
/// <param name="Kind">Which body of the turn this is.</param>
/// <param name="Sha256">The SHA-256 of the stored plaintext (after secrets were obscured), or <see langword="null"/> for a missing body.</param>
/// <param name="Length">The stored plaintext's length in bytes; zero for a missing body.</param>
/// <param name="Missing">Whether capture failed and the body was recorded as missing.</param>
/// <param name="Obscured">Whether the secret obscurer changed any byte, or <see langword="null"/> when that is not known.</param>
public sealed record SessionBodyHashRow(
    Guid ArchiveTurnId,
    SessionBodyKind Kind,
    byte[]? Sha256,
    long Length,
    bool Missing,
    bool? Obscured);

/// <summary>
/// One committed turn: its identity and where its frames sit in the session file. Holds no text.
/// </summary>
/// <param name="ArchiveTurnId">The turn's cross-machine identity.</param>
/// <param name="ArchiveSessionId">The session the turn belongs to.</param>
/// <param name="TurnSequence">Zero-based turn order inside the session.</param>
/// <param name="FirstFrameOrdinal">The zero-based position of the turn's first frame in the file.</param>
/// <param name="FrameCount">How many frames the turn occupies.</param>
/// <param name="EndOffset">The file length after the turn's last frame.</param>
/// <param name="CreatedAtUtc">When the turn was captured, or its original time when imported.</param>
/// <param name="Origin"><see cref="SessionTurnOrigin.Captured"/> or <see cref="SessionTurnOrigin.Imported"/>.</param>
public sealed record SessionTurnRow(
    Guid ArchiveTurnId,
    Guid ArchiveSessionId,
    int TurnSequence,
    long FirstFrameOrdinal,
    int FrameCount,
    long EndOffset,
    DateTimeOffset CreatedAtUtc,
    string Origin);

/// <summary>
/// What a conversation import records about a turn it stored, in the same transaction that commits the turn, so
/// a re-run of the same import can tell the turn is already present even when obscuring changed its bodies or a
/// conflict mode gave it a new local id.
/// </summary>
/// <param name="ImportId">The import that stored the turn.</param>
/// <param name="SourceArchiveTurnId">The turn's id in the zip, which differs from the local id when a conflict mode minted a new one.</param>
/// <param name="SourceDigest">
/// A SHA-256 over the turn's id and its exchange-body hashes as the zip declared them (see
/// <c>ConversationImporter.SourceDigest</c>), compared on a later import before the bodies are read.
/// </param>
/// <param name="CreatesSession">Whether this turn is the first of a session the import created, so deleting the import can remove the session.</param>
public sealed record SessionTurnImport(
    Guid ImportId, Guid SourceArchiveTurnId, byte[] SourceDigest, bool CreatesSession);

/// <summary>One past import as the index records it.</summary>
/// <param name="ImportId">The import's id.</param>
/// <param name="ImportedAtUtc">When it started.</param>
/// <param name="SourceSha256">Lowercase hex SHA-256 of the zip that was imported.</param>
/// <param name="SessionsAdded">Sessions it created.</param>
/// <param name="TurnsAdded">Turns it stored.</param>
/// <param name="TurnsSkipped">Turns that were already present.</param>
/// <param name="TurnsRejected">Turns that failed validation.</param>
public sealed record SessionImportRow(
    Guid ImportId,
    DateTimeOffset ImportedAtUtc,
    string SourceSha256,
    int SessionsAdded,
    int TurnsAdded,
    int TurnsSkipped,
    int TurnsRejected);

/// <summary>A turn a past import stored, found through the id it had in the zip.</summary>
/// <param name="ArchiveTurnId">The turn's local id.</param>
/// <param name="ImportId">The import that stored it.</param>
/// <param name="SourceDigest">The digest the zip declared for it.</param>
public sealed record SessionImportedTurn(Guid ArchiveTurnId, Guid ImportId, byte[] SourceDigest);

/// <summary>The values <see cref="SessionTurnRow.Origin"/> takes (#165 plan, "Import").</summary>
public static class SessionTurnOrigin
{
    /// <summary>The router served the turn.</summary>
    public const string Captured = "captured";

    /// <summary>The turn arrived through a conversation import.</summary>
    public const string Imported = "imported";
}

/// <summary>
/// The text-free SQLite index of ADR-0019 inside <c>transcripts.db</c>: one row per session file with its
/// wrapped key and committed extent, and one row per committed turn. It stores no prompt, reply, preview or
/// extract. A turn exists only once its row here has committed, which is what lets recovery tell committed
/// frames from frames a crash left in the file.
/// </summary>
/// <remarks>
/// Connections come from <see cref="TranscriptDatabase.OpenConnection"/>, so <c>secure_delete</c> applies:
/// deleting a session row zeroes its wrapped key's page content, and <see cref="TruncateWal"/> removes the
/// copy that may still sit in the write-ahead log.
/// </remarks>
public sealed class SessionIndex
{
    private const string SchemaSql = """
                                     CREATE TABLE IF NOT EXISTS session_files (
                                         archive_session_id TEXT    NOT NULL PRIMARY KEY,
                                         client_session_id  TEXT    NOT NULL,
                                         file_name          TEXT    NOT NULL UNIQUE,
                                         wrapped_key        BLOB    NOT NULL,
                                         committed_length   INTEGER NOT NULL,
                                         committed_frames   INTEGER NOT NULL,
                                         turn_count         INTEGER NOT NULL,
                                         created_at_utc     TEXT    NOT NULL,
                                         last_turn_at_utc   TEXT    NULL
                                     );

                                     CREATE INDEX IF NOT EXISTS ix_session_files_client_session_id
                                         ON session_files (client_session_id);

                                     CREATE TABLE IF NOT EXISTS session_turns (
                                         archive_turn_id    TEXT    NOT NULL PRIMARY KEY,
                                         archive_session_id TEXT    NOT NULL,
                                         turn_sequence      INTEGER NOT NULL,
                                         first_frame_ordinal INTEGER NOT NULL,
                                         frame_count        INTEGER NOT NULL,
                                         end_offset         INTEGER NOT NULL,
                                         created_at_utc     TEXT    NOT NULL,
                                         origin             TEXT    NOT NULL,
                                         UNIQUE (archive_session_id, turn_sequence)
                                     );

                                     CREATE TABLE IF NOT EXISTS session_bodies (
                                         archive_turn_id TEXT    NOT NULL,
                                         kind            INTEGER NOT NULL,
                                         sha256          BLOB    NULL,
                                         length          INTEGER NOT NULL,
                                         missing         INTEGER NOT NULL,
                                         obscured        INTEGER NULL,
                                         PRIMARY KEY (archive_turn_id, kind)
                                     );

                                     CREATE TABLE IF NOT EXISTS session_imports (
                                         import_id                  TEXT    NOT NULL PRIMARY KEY,
                                         imported_at_utc            TEXT    NOT NULL,
                                         source_sha256              TEXT    NOT NULL,
                                         options_json               TEXT    NOT NULL,
                                         sessions_added             INTEGER NOT NULL DEFAULT 0,
                                         turns_added                INTEGER NOT NULL DEFAULT 0,
                                         turns_skipped              INTEGER NOT NULL DEFAULT 0,
                                         turns_rejected             INTEGER NOT NULL DEFAULT 0,
                                         turns_removed_by_retention INTEGER NOT NULL DEFAULT 0
                                     );

                                     CREATE TABLE IF NOT EXISTS session_import_turns (
                                         archive_turn_id        TEXT    NOT NULL PRIMARY KEY,
                                         import_id              TEXT    NOT NULL,
                                         source_archive_turn_id TEXT    NOT NULL,
                                         source_digest          BLOB    NOT NULL
                                     );

                                     CREATE INDEX IF NOT EXISTS ix_session_import_turns_source
                                         ON session_import_turns (source_archive_turn_id);

                                     CREATE INDEX IF NOT EXISTS ix_session_import_turns_import
                                         ON session_import_turns (import_id);

                                     CREATE TABLE IF NOT EXISTS session_import_sessions (
                                         archive_session_id TEXT NOT NULL PRIMARY KEY,
                                         import_id          TEXT NOT NULL
                                     );
                                     """;

    private const int ExchangeKindCount = 4;
    private const int HashLength = 32;

    private readonly TranscriptDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionIndex"/> class.
    /// </summary>
    /// <param name="database">The transcript database that hosts the index tables.</param>
    public SessionIndex(TranscriptDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Creates the index tables if they are missing. Idempotent, and independent of
    /// <c>request_transcripts</c>, which this index leaves untouched until phase 2.
    /// </summary>
    public void EnsureCreated()
    {
        var directory = Path.GetDirectoryName(_database.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        using var connection = _database.OpenConnection();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = SchemaSql;
            schema.ExecuteNonQuery();
        }

        MigrateSessionSha256Column(connection);
        MigrateRetiredColumn(connection);
    }

    /// <summary>
    /// Adds <c>session_turns.retired</c> to an index created before overwriting imports existed. A retired turn
    /// stays in its file (the file is append-only) but leaves every read and the session checksum.
    /// </summary>
    /// <param name="connection">An open connection to the transcript database.</param>
    private static void MigrateRetiredColumn(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_turns') WHERE name = 'retired';";
            if (Convert.ToInt64(pragma.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE session_turns ADD COLUMN retired INTEGER NOT NULL DEFAULT 0;";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Retires a turn: it stays in its session file but leaves every read and the session checksum, which is
    /// recomputed in the same transaction (the chain cannot drop a link, so it is folded again from the turns that
    /// remain). An overwriting import calls this after it has stored the replacement.
    /// </summary>
    /// <param name="archiveTurnId">The turn to retire.</param>
    /// <returns><see langword="true"/> when a live turn was retired; <see langword="false"/> when it is unknown or already retired.</returns>
    public bool RetireTurn(Guid archiveTurnId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        string? sessionId;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                                 UPDATE session_turns SET retired = 1
                                 WHERE archive_turn_id = $turn AND retired = 0
                                 RETURNING archive_session_id;
                                 """;
            update.Parameters.AddWithValue("$turn", archiveTurnId.ToString("D"));
            sessionId = update.ExecuteScalar() as string;
        }

        if (sessionId is null) return false;

        UpdateSessionSha256(connection, transaction, Guid.Parse(sessionId));
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Adds <c>session_files.session_sha256</c> to an index created before body hashes existed. Same additive
    /// <c>PRAGMA table_info</c> check as the <c>TranscriptDatabase.Migrate*</c> methods; existing rows keep
    /// <see langword="null"/> until a backfill computes their checksum.
    /// </summary>
    /// <param name="connection">An open connection to the transcript database.</param>
    private static void MigrateSessionSha256Column(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_files') WHERE name = 'session_sha256';";
            if (Convert.ToInt64(pragma.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE session_files ADD COLUMN session_sha256 BLOB NULL;";
        alter.ExecuteNonQuery();
    }

    /// <summary>Records a newly created session file. Its key must be wrapped before any frame is written.</summary>
    /// <param name="row">The session's row; its extent is the bare header.</param>
    public void InsertSession(SessionFileRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO session_files (archive_session_id, client_session_id, file_name, wrapped_key,
                                                         committed_length, committed_frames, turn_count, created_at_utc,
                                                         last_turn_at_utc, session_sha256)
                              VALUES ($id, $client, $file, $key, $length, $frames, $turns, $created, $last, $sha);
                              """;
        command.Parameters.AddWithValue("$sha", (object?)row.SessionSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", row.ArchiveSessionId.ToString("D"));
        command.Parameters.AddWithValue("$client", row.ClientSessionId);
        command.Parameters.AddWithValue("$file", row.FileName);
        command.Parameters.AddWithValue("$key", row.WrappedKey);
        command.Parameters.AddWithValue("$length", row.CommittedLength);
        command.Parameters.AddWithValue("$frames", row.CommittedFrames);
        command.Parameters.AddWithValue("$turns", row.TurnCount);
        command.Parameters.AddWithValue("$created", Format(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$last", row.LastTurnAtUtc is { } last ? Format(last) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Looks up one session by its archive id.</summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns>The row, or <see langword="null"/> when the session is unknown.</returns>
    public SessionFileRow? TryGetSession(Guid archiveSessionId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSessionSql + " WHERE archive_session_id = $id;";
        command.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    /// <summary>
    /// Finds the newest session recorded for a client session id. The client id can repeat across machines,
    /// so this is only a local lookup for capture; import matches on the archive id. A session an import created
    /// is never returned: its client id came from another machine, and live traffic that happens to use the same id
    /// must start its own session rather than be appended to imported history.
    /// </summary>
    /// <param name="clientSessionId">The client's session id.</param>
    /// <returns>The newest matching row, or <see langword="null"/> when none exists.</returns>
    public SessionFileRow? FindNewestByClientSessionId(string clientSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSessionId);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSessionSql +
                              " WHERE client_session_id = $client"
                              + " AND archive_session_id NOT IN (SELECT archive_session_id FROM session_import_sessions)"
                              + " ORDER BY created_at_utc DESC, archive_session_id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$client", clientSessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    /// <summary>Lists every session in the index, oldest first.</summary>
    /// <returns>All session rows.</returns>
    public IReadOnlyList<SessionFileRow> ListSessions()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSessionSql + " ORDER BY created_at_utc, archive_session_id;";
        using var reader = command.ExecuteReader();
        var rows = new List<SessionFileRow>();
        while (reader.Read()) rows.Add(ReadSession(reader));
        return rows;
    }

    /// <summary>Lists a session's committed turns in turn order.</summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <param name="includeRetired">
    /// Whether to include turns an overwriting import retired. They stay in the file, so anything that walks the
    /// file's frames in order needs them; anything that reads the conversation does not.
    /// </param>
    /// <returns>The turns, oldest first.</returns>
    public IReadOnlyList<SessionTurnRow> ListTurns(Guid archiveSessionId, bool includeRetired = false)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectTurnSql + " WHERE archive_session_id = $id"
                              + (includeRetired ? string.Empty : " AND retired = 0") + " ORDER BY turn_sequence;";
        command.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
        using var reader = command.ExecuteReader();
        var rows = new List<SessionTurnRow>();
        while (reader.Read()) rows.Add(ReadTurn(reader));
        return rows;
    }

    /// <summary>
    /// Commits a turn: inserts its row and advances its session's committed extent in one transaction. This
    /// is the second half of the commit order, run only after the turn's frames were flushed to the file.
    /// </summary>
    /// <remarks>
    /// The turn's body hashes commit in the same transaction, and the session's <c>session_sha256</c> is
    /// advanced by one hash: it is a chain, <c>H(n) = SHA-256(H(n-1) || turn record)</c> with <c>H(0)</c> 32 zero
    /// bytes, where a turn record is the turn's 16-byte archive id (<see cref="Guid.TryWriteBytes(Span{byte})"/>
    /// layout) and then, for each exchange kind in the fixed order <see cref="SessionBodyKind.ClientRequest"/>,
    /// <see cref="SessionBodyKind.ClientResponse"/>, <see cref="SessionBodyKind.ProviderRequest"/>,
    /// <see cref="SessionBodyKind.ProviderResponse"/>, one byte for the kind and the body's 32-byte SHA-256,
    /// or 32 zero bytes when the body is missing or was never written. An append therefore costs one hash however
    /// long the session is, and the value depends only on the turns' ids and bodies in order. Metadata, extracts
    /// and every timestamp are excluded, so re-importing a session cannot change it. A session with a turn that
    /// has no body hashes at all (captured before they existed) keeps a <see langword="null"/> checksum until
    /// <see cref="ReplaceBodyHashes"/> backfills it, which folds every turn through the same chain.
    /// </remarks>
    /// <param name="turn">The turn's row; its sequence must equal the session's current turn count.</param>
    /// <param name="bodies">The hashes of every body the turn wrote, including metadata and extracts.</param>
    /// <param name="import">What an import records about the turn, committed with it; <see langword="null"/> for a captured turn.</param>
    public void CommitTurn(SessionTurnRow turn, IReadOnlyList<SessionBodyHashRow> bodies, SessionTurnImport? import = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(bodies);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var checksum = NextChecksum(connection, transaction, turn, bodies);

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                                 INSERT INTO session_turns (archive_turn_id, archive_session_id, turn_sequence,
                                                            first_frame_ordinal, frame_count, end_offset,
                                                            created_at_utc, origin)
                                 VALUES ($turn, $session, $seq, $first, $frames, $end, $created, $origin);
                                 """;
            insert.Parameters.AddWithValue("$turn", turn.ArchiveTurnId.ToString("D"));
            insert.Parameters.AddWithValue("$session", turn.ArchiveSessionId.ToString("D"));
            insert.Parameters.AddWithValue("$seq", turn.TurnSequence);
            insert.Parameters.AddWithValue("$first", turn.FirstFrameOrdinal);
            insert.Parameters.AddWithValue("$frames", turn.FrameCount);
            insert.Parameters.AddWithValue("$end", turn.EndOffset);
            insert.Parameters.AddWithValue("$created", Format(turn.CreatedAtUtc));
            insert.Parameters.AddWithValue("$origin", turn.Origin);
            insert.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                                 UPDATE session_files
                                 SET committed_length = $end,
                                     committed_frames = $firstPlusCount,
                                     turn_count = turn_count + 1,
                                     session_sha256 = $sha,
                                     last_turn_at_utc = CASE WHEN last_turn_at_utc IS NULL OR last_turn_at_utc < $created
                                                             THEN $created ELSE last_turn_at_utc END
                                 WHERE archive_session_id = $session AND turn_count = $seq;
                                 """;
            update.Parameters.AddWithValue("$sha", checksum is null ? DBNull.Value : checksum);
            update.Parameters.AddWithValue("$end", turn.EndOffset);
            update.Parameters.AddWithValue("$firstPlusCount", turn.FirstFrameOrdinal + turn.FrameCount);
            update.Parameters.AddWithValue("$created", Format(turn.CreatedAtUtc));
            update.Parameters.AddWithValue("$session", turn.ArchiveSessionId.ToString("D"));
            update.Parameters.AddWithValue("$seq", turn.TurnSequence);
            if (update.ExecuteNonQuery() != 1)
            {
                // Rolled back with the transaction: the session is gone or another writer got this sequence.
                throw new InvalidOperationException("The session's turn count does not match the turn being committed.");
            }
        }

        InsertBodies(connection, transaction, bodies);

        if (import is not null)
        {
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                                 INSERT INTO session_import_turns (archive_turn_id, import_id, source_archive_turn_id, source_digest)
                                 VALUES ($turn, $import, $source, $digest);
                                 """;
            record.Parameters.AddWithValue("$turn", turn.ArchiveTurnId.ToString("D"));
            record.Parameters.AddWithValue("$import", import.ImportId.ToString("D"));
            record.Parameters.AddWithValue("$source", import.SourceArchiveTurnId.ToString("D"));
            record.Parameters.AddWithValue("$digest", import.SourceDigest);
            record.ExecuteNonQuery();

            if (import.CreatesSession)
            {
                using var created = connection.CreateCommand();
                created.Transaction = transaction;
                created.CommandText = """
                                      INSERT OR IGNORE INTO session_import_sessions (archive_session_id, import_id)
                                      VALUES ($session, $import);
                                      """;
                created.Parameters.AddWithValue("$session", turn.ArchiveSessionId.ToString("D"));
                created.Parameters.AddWithValue("$import", import.ImportId.ToString("D"));
                created.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Records that an import has started, with zero counts, so the turns it stores have a row to point at. The
    /// counts are filled in by <see cref="CompleteImport"/>; a crash in between leaves the zeros.
    /// </summary>
    /// <param name="importId">The import's id.</param>
    /// <param name="importedAtUtc">When the import started.</param>
    /// <param name="sourceSha256Hex">Lowercase hex SHA-256 of the zip that was imported.</param>
    /// <param name="optionsJson">The options the import ran with, as JSON.</param>
    public void BeginImport(Guid importId, DateTimeOffset importedAtUtc, string sourceSha256Hex, string optionsJson)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO session_imports (import_id, imported_at_utc, source_sha256, options_json)
                              VALUES ($id, $at, $sha, $options);
                              """;
        command.Parameters.AddWithValue("$id", importId.ToString("D"));
        command.Parameters.AddWithValue("$at", Format(importedAtUtc));
        command.Parameters.AddWithValue("$sha", sourceSha256Hex);
        command.Parameters.AddWithValue("$options", optionsJson);
        command.ExecuteNonQuery();
    }

    /// <summary>Writes an import's final counts.</summary>
    /// <param name="importId">The import's id.</param>
    /// <param name="sessionsAdded">Sessions the import created.</param>
    /// <param name="turnsAdded">Turns the import stored.</param>
    /// <param name="turnsSkipped">Turns already present.</param>
    /// <param name="turnsRejected">Turns that failed validation.</param>
    public void CompleteImport(Guid importId, int sessionsAdded, int turnsAdded, int turnsSkipped, int turnsRejected)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              UPDATE session_imports
                              SET sessions_added = $sessions, turns_added = $added,
                                  turns_skipped = $skipped, turns_rejected = $rejected
                              WHERE import_id = $id;
                              """;
        command.Parameters.AddWithValue("$id", importId.ToString("D"));
        command.Parameters.AddWithValue("$sessions", sessionsAdded);
        command.Parameters.AddWithValue("$added", turnsAdded);
        command.Parameters.AddWithValue("$skipped", turnsSkipped);
        command.Parameters.AddWithValue("$rejected", turnsRejected);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Counts each session's live turns (those not retired) and finds its newest live turn's time. Retiring a turn leaves
    /// <c>session_files.turn_count</c> alone, because that is also the next physical sequence, so anything that decides by
    /// how much history a session holds must read these instead.
    /// </summary>
    /// <returns>The live turn count and newest live turn time of every session that has a live turn.</returns>
    public IReadOnlyDictionary<Guid, (int Count, DateTimeOffset Last)> LiveTurnStats()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT archive_session_id, COUNT(*), MAX(created_at_utc)
                              FROM session_turns WHERE retired = 0 GROUP BY archive_session_id;
                              """;
        using var reader = command.ExecuteReader();
        var stats = new Dictionary<Guid, (int, DateTimeOffset)>();
        while (reader.Read()) stats[Guid.Parse(reader.GetString(0))] = (reader.GetInt32(1), Parse(reader.GetString(2)));
        return stats;
    }

    /// <summary>Lists past imports, newest first.</summary>
    /// <returns>One row per import.</returns>
    public IReadOnlyList<SessionImportRow> ListImports()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT import_id, imported_at_utc, source_sha256, sessions_added, turns_added,
                                     turns_skipped, turns_rejected
                              FROM session_imports ORDER BY imported_at_utc DESC, import_id DESC;
                              """;
        using var reader = command.ExecuteReader();
        var rows = new List<SessionImportRow>();
        while (reader.Read())
        {
            rows.Add(new SessionImportRow(
                Guid.Parse(reader.GetString(0)), Parse(reader.GetString(1)), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        }

        return rows;
    }

    /// <summary>Lists the local ids of the turns an import stored that are still in the index.</summary>
    /// <param name="importId">The import.</param>
    /// <returns>The turns' local ids.</returns>
    public IReadOnlyList<Guid> ListImportTurnIds(Guid importId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT archive_turn_id FROM session_import_turns WHERE import_id = $import;";
        command.Parameters.AddWithValue("$import", importId.ToString("D"));
        using var reader = command.ExecuteReader();
        var ids = new List<Guid>();
        while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        return ids;
    }

    /// <summary>Lists the sessions an import created that still exist.</summary>
    /// <param name="importId">The import.</param>
    /// <returns>The sessions' archive ids.</returns>
    public IReadOnlyList<Guid> ListImportSessionIds(Guid importId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT archive_session_id FROM session_import_sessions WHERE import_id = $import;";
        command.Parameters.AddWithValue("$import", importId.ToString("D"));
        using var reader = command.ExecuteReader();
        var ids = new List<Guid>();
        while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        return ids;
    }

    /// <summary>Removes every record of an import: its row and the links to the turns and sessions it stored.</summary>
    /// <param name="importId">The import.</param>
    /// <returns><see langword="true"/> when the import was known.</returns>
    public bool DeleteImportRecords(Guid importId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              DELETE FROM session_import_turns WHERE import_id = $import;
                              DELETE FROM session_import_sessions WHERE import_id = $import;
                              DELETE FROM session_imports WHERE import_id = $import;
                              """;
        command.Parameters.AddWithValue("$import", importId.ToString("D"));
        var removed = command.ExecuteNonQuery();
        transaction.Commit();
        return removed > 0;
    }

    /// <summary>Finds the turns past imports stored for a zip turn id.</summary>
    /// <param name="sourceArchiveTurnId">The turn's id in the zip.</param>
    /// <returns>Each matching local turn with the digest its zip declared; empty when none was imported.</returns>
    public IReadOnlyList<SessionImportedTurn> ListImportedBySource(Guid sourceArchiveTurnId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT archive_turn_id, import_id, source_digest
                              FROM session_import_turns WHERE source_archive_turn_id = $source;
                              """;
        command.Parameters.AddWithValue("$source", sourceArchiveTurnId.ToString("D"));
        using var reader = command.ExecuteReader();
        var rows = new List<SessionImportedTurn>();
        while (reader.Read())
        {
            rows.Add(new SessionImportedTurn(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), (byte[])reader["source_digest"]));
        }

        return rows;
    }

    /// <summary>
    /// Computes the session checksum after <paramref name="turn"/> by chaining it onto the stored one, so an append
    /// costs one hash however long the session is.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The commit's transaction, which the read joins.</param>
    /// <param name="turn">The turn being committed.</param>
    /// <param name="bodies">The hashes of every body the turn wrote.</param>
    /// <returns>
    /// The new checksum, or <see langword="null"/> when an earlier turn has no body hashes (the session predates
    /// them), which stays <see langword="null"/> until a backfill.
    /// </returns>
    private static byte[]? NextChecksum(
        SqliteConnection connection, SqliteTransaction transaction, SessionTurnRow turn, IReadOnlyList<SessionBodyHashRow> bodies)
    {
        byte[]? previous;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT turn_count, session_sha256 FROM session_files WHERE archive_session_id = $id;";
            read.Parameters.AddWithValue("$id", turn.ArchiveSessionId.ToString("D"));
            using var reader = read.ExecuteReader();

            // An unknown session fails the update that follows, with the usual message.
            if (!reader.Read()) return null;

            previous = reader.GetInt32(0) == 0 ? new byte[HashLength] : reader.IsDBNull(1) ? null : (byte[])reader["session_sha256"];
        }

        if (previous is null) return null;

        var slots = new SessionBodyHashRow?[ExchangeKindCount];
        foreach (var body in bodies)
        {
            if ((int)body.Kind is >= 1 and <= ExchangeKindCount) slots[(int)body.Kind - 1] = body;
        }

        return ChainTurn(previous, turn.ArchiveTurnId, slots);
    }

    /// <summary>
    /// Folds one turn into the running session checksum: SHA-256 of the previous checksum, the turn's 16-byte id,
    /// and for each exchange kind one byte for the kind and the body's 32-byte SHA-256, or 32 zero bytes when the
    /// body is missing or was never written.
    /// </summary>
    /// <param name="previous">The checksum before this turn (32 zero bytes for the first turn).</param>
    /// <param name="archiveTurnId">The turn's archive id.</param>
    /// <param name="slots">The turn's exchange body rows by kind order, with null for a kind it does not have.</param>
    /// <returns>The checksum after this turn.</returns>
    private static byte[] ChainTurn(byte[] previous, Guid archiveTurnId, SessionBodyHashRow?[] slots)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> id = stackalloc byte[16];
        Span<byte> kindByte = stackalloc byte[1];
        Span<byte> zero = stackalloc byte[HashLength];
        hash.AppendData(previous);
        archiveTurnId.TryWriteBytes(id);
        hash.AppendData(id);
        for (var i = 0; i < ExchangeKindCount; i++)
        {
            kindByte[0] = (byte)(i + 1);
            hash.AppendData(kindByte);
            if (slots[i] is { Missing: false, Sha256: { Length: HashLength } digest }) hash.AppendData(digest);
            else hash.AppendData(zero);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Lists the hashes of every body of a session, in turn order and then kind order.
    /// </summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns>The body hash rows; empty for an unknown session or one with no hashes yet.</returns>
    public IReadOnlyList<SessionBodyHashRow> ListBodiesForSession(Guid archiveSessionId)
    {
        using var connection = _database.OpenConnection();
        return ListBodies(connection, transaction: null, archiveSessionId);
    }

    /// <summary>
    /// Reports whether every committed turn of a session has body hashes. A session captured before hashes
    /// existed answers <see langword="false"/> until <see cref="ReplaceBodyHashes"/> backfills it; a session
    /// with no turns answers <see langword="true"/>, because there is nothing to backfill.
    /// </summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns><see langword="true"/> when no turn of the session lacks hash rows.</returns>
    public bool HasBodyHashes(Guid archiveSessionId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*) FROM session_turns t
                              WHERE t.archive_session_id = $id
                                AND NOT EXISTS (SELECT 1 FROM session_bodies b WHERE b.archive_turn_id = t.archive_turn_id);
                              """;
        command.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
    }

    /// <summary>
    /// Replaces a session's body hashes and recomputes its checksum in one transaction. This is the backfill
    /// for a session captured before hashes existed, and it also repairs a session whose hashes were lost.
    /// The caller holds the session's gate so no turn is appended while it runs.
    /// </summary>
    /// <param name="archiveSessionId">The session whose hashes are replaced.</param>
    /// <param name="bodies">The hashes of every body of every committed turn of the session.</param>
    public void ReplaceBodyHashes(Guid archiveSessionId, IReadOnlyList<SessionBodyHashRow> bodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                                 DELETE FROM session_bodies
                                 WHERE archive_turn_id IN (SELECT archive_turn_id FROM session_turns WHERE archive_session_id = $id);
                                 """;
            delete.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
            delete.ExecuteNonQuery();
        }

        InsertBodies(connection, transaction, bodies);
        UpdateSessionSha256(connection, transaction, archiveSessionId);
        transaction.Commit();
    }

    /// <summary>Reads a session's stored checksum.</summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns>The 32-byte checksum, or <see langword="null"/> when the session is unknown or has no checksum yet.</returns>
    public byte[]? GetSessionSha256(Guid archiveSessionId) => TryGetSession(archiveSessionId)?.SessionSha256;

    /// <summary>Looks up one committed turn by its archive id.</summary>
    /// <param name="archiveTurnId">The turn's archive id.</param>
    /// <returns>The turn, or <see langword="null"/> when it is not in the index.</returns>
    public SessionTurnRow? TryGetTurn(Guid archiveTurnId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectTurnSql + " WHERE archive_turn_id = $turn;";
        command.Parameters.AddWithValue("$turn", archiveTurnId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTurn(reader) : null;
    }

    /// <summary>Looks up one committed turn by its position in its session.</summary>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <param name="turnSequence">The zero-based turn order.</param>
    /// <returns>The turn, or <see langword="null"/> when the session has no such turn.</returns>
    public SessionTurnRow? TryGetTurnBySequence(Guid archiveSessionId, int turnSequence)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectTurnSql + " WHERE archive_session_id = $id AND turn_sequence = $seq;";
        command.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
        command.Parameters.AddWithValue("$seq", turnSequence);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTurn(reader) : null;
    }

    /// <summary>Inserts body hash rows inside a transaction.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The transaction the rows join.</param>
    /// <param name="bodies">The rows to insert.</param>
    private static void InsertBodies(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SessionBodyHashRow> bodies)
    {
        if (bodies.Count == 0) return;

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
                             INSERT INTO session_bodies (archive_turn_id, kind, sha256, length, missing, obscured)
                             VALUES ($turn, $kind, $sha, $length, $missing, $obscured);
                             """;
        var turn = insert.Parameters.Add("$turn", SqliteType.Text);
        var kind = insert.Parameters.Add("$kind", SqliteType.Integer);
        var sha = insert.Parameters.Add("$sha", SqliteType.Blob);
        var length = insert.Parameters.Add("$length", SqliteType.Integer);
        var missing = insert.Parameters.Add("$missing", SqliteType.Integer);
        var obscured = insert.Parameters.Add("$obscured", SqliteType.Integer);
        foreach (var body in bodies)
        {
            turn.Value = body.ArchiveTurnId.ToString("D");
            kind.Value = (int)body.Kind;
            sha.Value = (object?)body.Sha256 ?? DBNull.Value;
            length.Value = body.Length;
            missing.Value = body.Missing ? 1 : 0;
            obscured.Value = body.Obscured is { } changed ? (changed ? 1 : 0) : DBNull.Value;
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Lists a session's body hash rows in turn order, then kind order.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The transaction to read within, or <see langword="null"/>.</param>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <returns>The rows.</returns>
    private static List<SessionBodyHashRow> ListBodies(
        SqliteConnection connection, SqliteTransaction? transaction, Guid archiveSessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              SELECT b.archive_turn_id, b.kind, b.sha256, b.length, b.missing, b.obscured
                              FROM session_bodies b
                              JOIN session_turns t ON t.archive_turn_id = b.archive_turn_id
                              WHERE t.archive_session_id = $id AND t.retired = 0
                              ORDER BY t.turn_sequence, b.kind;
                              """;
        command.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
        using var reader = command.ExecuteReader();
        var rows = new List<SessionBodyHashRow>();
        while (reader.Read())
        {
            rows.Add(new SessionBodyHashRow(
                ArchiveTurnId: Guid.Parse(reader.GetString(0)),
                Kind: (SessionBodyKind)reader.GetInt32(1),
                Sha256: reader.IsDBNull(2) ? null : (byte[])reader["sha256"],
                Length: reader.GetInt64(3),
                Missing: reader.GetInt32(4) != 0,
                Obscured: reader.IsDBNull(5) ? null : reader.GetInt32(5) != 0));
        }

        return rows;
    }

    /// <summary>
    /// Recomputes <c>session_files.session_sha256</c> from the session's body hashes, or clears it when a
    /// turn has none. Runs inside the transaction that changed the hashes, so the checksum never disagrees
    /// with the rows it covers.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The transaction the change belongs to.</param>
    /// <param name="archiveSessionId">The session to update.</param>
    private static void UpdateSessionSha256(
        SqliteConnection connection, SqliteTransaction transaction, Guid archiveSessionId)
    {
        var turnIds = new List<Guid>();
        using (var turns = connection.CreateCommand())
        {
            turns.Transaction = transaction;
            turns.CommandText = "SELECT archive_turn_id FROM session_turns WHERE archive_session_id = $id AND retired = 0 ORDER BY turn_sequence;";
            turns.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
            using var reader = turns.ExecuteReader();
            while (reader.Read()) turnIds.Add(Guid.Parse(reader.GetString(0)));
        }

        var byTurn = new Dictionary<Guid, SessionBodyHashRow?[]>();
        foreach (var row in ListBodies(connection, transaction, archiveSessionId))
        {
            if ((int)row.Kind is < 1 or > ExchangeKindCount)
            {
                // Metadata and extracts are not part of the checksum, but they count as "this turn has hashes".
                byTurn.TryAdd(row.ArchiveTurnId, new SessionBodyHashRow?[ExchangeKindCount]);
                continue;
            }

            if (!byTurn.TryGetValue(row.ArchiveTurnId, out var slots))
            {
                slots = new SessionBodyHashRow?[ExchangeKindCount];
                byTurn[row.ArchiveTurnId] = slots;
            }

            slots[(int)row.Kind - 1] = row;
        }

        byte[]? checksum = null;
        if (turnIds.TrueForAll(byTurn.ContainsKey))
        {
            checksum = new byte[HashLength];
            foreach (var turnId in turnIds) checksum = ChainTurn(checksum, turnId, byTurn[turnId]);
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE session_files SET session_sha256 = $sha WHERE archive_session_id = $id;";
        update.Parameters.AddWithValue("$sha", checksum is null ? DBNull.Value : checksum);
        update.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
        update.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes a session's index rows, including the wrapped key. Under <c>secure_delete</c> the freed
    /// content is zeroed; the caller then calls <see cref="TruncateWal"/> and rotates the master key.
    /// </summary>
    /// <param name="archiveSessionId">The session to remove.</param>
    /// <returns>The deleted session's file name, or <see langword="null"/> when it was not in the index.</returns>
    public string? DeleteSession(Guid archiveSessionId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        string? fileName;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT file_name FROM session_files WHERE archive_session_id = $id;";
            select.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
            fileName = select.ExecuteScalar() as string;
        }

        if (fileName is null) return null;

        // Before the turns, because the rows are found through them. The import records go too, so a deleted
        // session is not later mistaken for one that is still present.
        using (var imported = connection.CreateCommand())
        {
            imported.Transaction = transaction;
            imported.CommandText = """
                                   DELETE FROM session_import_turns
                                   WHERE archive_turn_id IN (SELECT archive_turn_id FROM session_turns WHERE archive_session_id = $id);
                                   DELETE FROM session_import_sessions WHERE archive_session_id = $id;
                                   """;
            imported.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
            imported.ExecuteNonQuery();
        }

        using (var bodies = connection.CreateCommand())
        {
            bodies.Transaction = transaction;
            bodies.CommandText = """
                                 DELETE FROM session_bodies
                                 WHERE archive_turn_id IN (SELECT archive_turn_id FROM session_turns WHERE archive_session_id = $id);
                                 """;
            bodies.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
            bodies.ExecuteNonQuery();
        }

        using (var turns = connection.CreateCommand())
        {
            turns.Transaction = transaction;
            turns.CommandText = "DELETE FROM session_turns WHERE archive_session_id = $id;";
            turns.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
            turns.ExecuteNonQuery();
        }

        using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = "DELETE FROM session_files WHERE archive_session_id = $id;";
            session.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
            session.ExecuteNonQuery();
        }

        transaction.Commit();
        return fileName;
    }

    /// <summary>
    /// Re-wraps every session key in one transaction, so a crash leaves all keys wrapped under the old
    /// master key or all under the new one, never a mixture.
    /// </summary>
    /// <param name="rewrap">Maps an old wrapped key to its replacement under the new master key.</param>
    /// <returns>How many keys were re-wrapped.</returns>
    public int RewrapAllKeys(Func<byte[], byte[]> rewrap)
    {
        ArgumentNullException.ThrowIfNull(rewrap);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var current = new List<(string Id, byte[] Key)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT archive_session_id, wrapped_key FROM session_files;";
            using var reader = select.ExecuteReader();
            while (reader.Read()) current.Add((reader.GetString(0), (byte[])reader["wrapped_key"]));
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE session_files SET wrapped_key = $key WHERE archive_session_id = $id;";
        var keyParameter = update.Parameters.Add("$key", SqliteType.Blob);
        var idParameter = update.Parameters.Add("$id", SqliteType.Text);
        foreach (var (id, key) in current)
        {
            keyParameter.Value = rewrap(key);
            idParameter.Value = id;
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return current.Count;
    }

    /// <summary>
    /// Checkpoints and truncates the write-ahead log so a deleted wrapped key cannot linger in the
    /// <c>-wal</c> file (ADR-0024).
    /// </summary>
    /// <returns><see langword="true"/> when the log was truncated; <see langword="false"/> when it was busy and the caller must retry.</returns>
    public bool TruncateWal()
    {
        using var connection = _database.OpenConnection();
        return SqliteHardening.TruncateWal(connection);
    }

    private const string SelectSessionSql = """
                                            SELECT archive_session_id, client_session_id, file_name, wrapped_key, committed_length,
                                                   committed_frames, turn_count, created_at_utc, last_turn_at_utc,
                                                   session_sha256
                                            FROM session_files
                                            """;

    private const string SelectTurnSql = """
                                         SELECT archive_turn_id, archive_session_id, turn_sequence, first_frame_ordinal,
                                                frame_count, end_offset, created_at_utc, origin
                                         FROM session_turns
                                         """;

    private static SessionTurnRow ReadTurn(SqliteDataReader reader) => new(
        ArchiveTurnId: Guid.Parse(reader.GetString(0)),
        ArchiveSessionId: Guid.Parse(reader.GetString(1)),
        TurnSequence: reader.GetInt32(2),
        FirstFrameOrdinal: reader.GetInt64(3),
        FrameCount: reader.GetInt32(4),
        EndOffset: reader.GetInt64(5),
        CreatedAtUtc: Parse(reader.GetString(6)),
        Origin: reader.GetString(7));

    private static SessionFileRow ReadSession(SqliteDataReader reader) => new(
        ArchiveSessionId: Guid.Parse(reader.GetString(0)),
        ClientSessionId: reader.GetString(1),
        FileName: reader.GetString(2),
        WrappedKey: (byte[])reader["wrapped_key"],
        CommittedLength: reader.GetInt64(4),
        CommittedFrames: reader.GetInt64(5),
        TurnCount: reader.GetInt32(6),
        CreatedAtUtc: Parse(reader.GetString(7)),
        LastTurnAtUtc: reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
        SessionSha256: reader.IsDBNull(9) ? null : (byte[])reader["session_sha256"]);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
