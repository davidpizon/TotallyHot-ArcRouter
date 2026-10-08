using System.Globalization;
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
public sealed record SessionFileRow(
    Guid ArchiveSessionId,
    string ClientSessionId,
    string FileName,
    byte[] WrappedKey,
    long CommittedLength,
    long CommittedFrames,
    int TurnCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastTurnAtUtc);

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
                                     """;

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

        using var schema = connection.CreateCommand();
        schema.CommandText = SchemaSql;
        schema.ExecuteNonQuery();
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
                                                         last_turn_at_utc)
                              VALUES ($id, $client, $file, $key, $length, $frames, $turns, $created, $last);
                              """;
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
    /// so this is only a local lookup for capture; import matches on the archive id.
    /// </summary>
    /// <param name="clientSessionId">The client's session id.</param>
    /// <returns>The newest matching row, or <see langword="null"/> when none exists.</returns>
    public SessionFileRow? FindNewestByClientSessionId(string clientSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSessionId);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSessionSql +
                              " WHERE client_session_id = $client ORDER BY created_at_utc DESC, archive_session_id DESC LIMIT 1;";
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
    /// <returns>The turns, oldest first.</returns>
    public IReadOnlyList<SessionTurnRow> ListTurns(Guid archiveSessionId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT archive_turn_id, archive_session_id, turn_sequence, first_frame_ordinal,
                                     frame_count, end_offset, created_at_utc, origin
                              FROM session_turns WHERE archive_session_id = $id ORDER BY turn_sequence;
                              """;
        command.Parameters.AddWithValue("$id", archiveSessionId.ToString("D"));
        using var reader = command.ExecuteReader();
        var rows = new List<SessionTurnRow>();
        while (reader.Read())
        {
            rows.Add(new SessionTurnRow(
                ArchiveTurnId: Guid.Parse(reader.GetString(0)),
                ArchiveSessionId: Guid.Parse(reader.GetString(1)),
                TurnSequence: reader.GetInt32(2),
                FirstFrameOrdinal: reader.GetInt64(3),
                FrameCount: reader.GetInt32(4),
                EndOffset: reader.GetInt64(5),
                CreatedAtUtc: Parse(reader.GetString(6)),
                Origin: reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>
    /// Commits a turn: inserts its row and advances its session's committed extent in one transaction. This
    /// is the second half of the commit order, run only after the turn's frames were flushed to the file.
    /// </summary>
    /// <param name="turn">The turn's row; its sequence must equal the session's current turn count.</param>
    public void CommitTurn(SessionTurnRow turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

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
                                     last_turn_at_utc = CASE WHEN last_turn_at_utc IS NULL OR last_turn_at_utc < $created
                                                             THEN $created ELSE last_turn_at_utc END
                                 WHERE archive_session_id = $session AND turn_count = $seq;
                                 """;
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

        transaction.Commit();
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
                                                   committed_frames, turn_count, created_at_utc, last_turn_at_utc
                                            FROM session_files
                                            """;

    private static SessionFileRow ReadSession(SqliteDataReader reader) => new(
        ArchiveSessionId: Guid.Parse(reader.GetString(0)),
        ClientSessionId: reader.GetString(1),
        FileName: reader.GetString(2),
        WrappedKey: (byte[])reader["wrapped_key"],
        CommittedLength: reader.GetInt64(4),
        CommittedFrames: reader.GetInt64(5),
        TurnCount: reader.GetInt32(6),
        CreatedAtUtc: Parse(reader.GetString(7)),
        LastTurnAtUtc: reader.IsDBNull(8) ? null : Parse(reader.GetString(8)));

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
