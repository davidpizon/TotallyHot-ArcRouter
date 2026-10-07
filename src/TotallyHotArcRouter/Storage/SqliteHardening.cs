using Microsoft.Data.Sqlite;

namespace TotallyHot.ArcRouter.Storage;

/// <summary>
/// The one place every router database opens a connection, so the settings that make deletion final
/// are applied to every connection and not only to the one that created the schema (#184, ADR-0024).
/// </summary>
/// <remarks>
/// <para>
/// SQLite pragmas are per connection: only <c>journal_mode=WAL</c> persists in the file. A
/// <c>secure_delete</c> set once in <c>EnsureCreated</c> therefore never reaches the connections that
/// later perform the deletes, and the harness behind the plan measured 298 of 300 cleared rows still
/// readable in that case. Setting both pragmas here removes that failure mode for every database
/// (decisions 6 and 8 of the plan).
/// </para>
/// <para>
/// <c>secure_delete=ON</c> zeroes freed content, but the content may still sit in the write-ahead log
/// until a checkpoint copies it back and truncates the log, so a delete is not final until
/// <see cref="TruncateWal(SqliteConnection)"/> reports success.
/// </para>
/// </remarks>
internal static class SqliteHardening
{
    /// <summary>
    /// Opens a connection and applies the per-connection pragmas. The caller owns disposal.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <returns>An open connection with <c>secure_delete=ON</c> and <c>synchronous=NORMAL</c>.</returns>
    internal static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA secure_delete=ON; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs <c>PRAGMA wal_checkpoint(TRUNCATE)</c>, which copies the log's pages into the database and
    /// resets the log to zero bytes, so deleted content cannot linger in the <c>-wal</c> file.
    /// </summary>
    /// <param name="connection">An open connection to the database.</param>
    /// <returns>
    /// <see langword="true"/> when the checkpoint ran to completion; <see langword="false"/> when another
    /// connection held the log (SQLite's <c>busy</c> flag), meaning the deletion is not yet final and
    /// the caller must retry.
    /// </returns>
    internal static bool TruncateWal(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Microsoft.Data.Sqlite gives every connection a 30-second busy timeout, and a TRUNCATE checkpoint
        // waits out a reader for all of it. A purge must not stall that long on a reader, so the checkpoint
        // runs with no wait and reports busy instead; callers retry. The previous timeout is restored
        // because a pooled connection keeps it.
        long previousTimeout;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA busy_timeout;";
            previousTimeout = (long)read.ExecuteScalar()!;
        }

        try
        {
            using var set = connection.CreateCommand();
            set.CommandText = "PRAGMA busy_timeout=0;";
            set.ExecuteNonQuery();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = command.ExecuteReader();
            return reader.Read() && reader.GetInt64(0) == 0;
        }
        finally
        {
            using var restore = connection.CreateCommand();
            restore.CommandText = $"PRAGMA busy_timeout={previousTimeout};";
            restore.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Opens a short-lived connection to <paramref name="connectionString"/> and truncates its log.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <returns>The result of <see cref="TruncateWal(SqliteConnection)"/>.</returns>
    internal static bool TruncateWal(string connectionString)
    {
        using var connection = Open(connectionString);
        return TruncateWal(connection);
    }
}
