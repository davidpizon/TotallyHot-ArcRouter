using Microsoft.Data.Sqlite;

namespace TotallyHot.ArcRouter.Storage;

/// <summary>
/// The one-time scrub that removes text deleted before <c>secure_delete</c> was switched on (#184,
/// ADR-0024). <c>secure_delete</c> zeroes content only at the moment a page is freed, so pages freed
/// earlier still hold the old text until the file is rebuilt: the harness behind the plan found 298 of 300
/// cleared rows readable after Clear and a restart. <c>VACUUM</c> followed by a log truncation rebuilds the
/// file and leaves nothing behind.
/// </summary>
/// <remarks>
/// <para>
/// <c>VACUUM</c> builds a full copy of the database, then writes it back through the log, so it needs about
/// twice the database's size on top of the file. The scrub therefore (a) uses <c>temp_store=FILE</c>, so
/// the transient copy never sits in memory, and (b) checks free space first. Where that copy lands is the
/// caller's business: <see cref="ScrubProcessLauncher"/> runs the scrub in a child process whose
/// <c>TMP</c>, <c>TEMP</c>, <c>TMPDIR</c> and <c>SQLITE_TMPDIR</c> point at a folder in the protected data
/// directory. SQLite's temporary-directory setting is process-wide and unsafe to change while other
/// connections are live, so the live host never touches it.
/// </para>
/// <para>
/// A scrub that cannot start or finish the rebuild is <see cref="Outcome.Deferred"/>, not skipped: it is
/// logged and retried at the next start. A rebuild that succeeds counts as done even if the log could not
/// be truncated yet, because repeating the rebuild would not help. Completion is recorded by a marker file
/// beside the database, so the scrub runs once per database.
/// </para>
/// </remarks>
internal static class SqliteScrub
{
    private const long OneGibibyte = 1024L * 1024 * 1024;

    /// <summary>The result of one <see cref="Run"/>.</summary>
    internal enum Outcome
    {
        /// <summary>The marker already existed, or there is no database to scrub.</summary>
        NotNeeded,

        /// <summary>The database was rebuilt and the marker written. The log may still await a later truncation.</summary>
        Completed,

        /// <summary>The scrub could not start or finish and will be tried again at the next start.</summary>
        Deferred,
    }

    /// <summary>
    /// Reports free and total bytes on the volume holding a path. A seam so a test can simulate a full disk.
    /// </summary>
    /// <param name="Free">Bytes available to this process.</param>
    /// <param name="Total">The volume's total size in bytes.</param>
    internal readonly record struct VolumeSpace(long Free, long Total);

    /// <summary>
    /// Scrubs <paramref name="databasePath"/> once, unless <paramref name="markerPath"/> already records that
    /// it was done.
    /// </summary>
    /// <param name="databasePath">The SQLite file to rebuild.</param>
    /// <param name="markerPath">The file whose existence records a completed scrub.</param>
    /// <param name="logger">The logger, for the deferred and completed outcomes.</param>
    /// <param name="probeVolume">Overrides the free-space probe; <see langword="null"/> asks the operating system.</param>
    /// <returns>What happened.</returns>
    internal static Outcome Run(
        string databasePath,
        string markerPath,
        ILogger logger,
        Func<string, VolumeSpace>? probeVolume = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        ArgumentNullException.ThrowIfNull(logger);

        if (File.Exists(markerPath)) return Outcome.NotNeeded;

        // No file means nothing was ever freed into it: every page it will ever hold is written under
        // secure_delete from the start.
        if (!File.Exists(databasePath))
        {
            WriteMarker(markerPath);
            return Outcome.NotNeeded;
        }

        var size = FileLength(databasePath) + FileLength(databasePath + "-wal");
        var space = (probeVolume ?? ProbeVolume)(databasePath);
        var reserve = Math.Max(val1: OneGibibyte, val2: space.Total / 10);
        var needed = 2 * size + reserve;
        if (space.Free < needed)
        {
            logger.LogWarning(
                "Deferring the one-time scrub of {DatabasePath}: it needs {RequiredBytes} bytes free and {FreeBytes} are available.",
                databasePath, needed, space.Free);
            return Outcome.Deferred;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ToString();
            using var connection = SqliteHardening.Open(connectionString);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA temp_store=FILE; VACUUM;";
                command.ExecuteNonQuery();
            }

            // The rebuilt file is already clean, so a busy log does not undo the scrub and must not make the
            // next start repeat the whole VACUUM. Only the log's old frames are pending, and the startup
            // checkpoint, Clear and the retention purge each truncate it again.
            if (!SqliteHardening.TruncateWal(connection))
                logger.LogWarning(
                    "The one-time scrub of {DatabasePath} rebuilt the file but the write-ahead log was busy; the log will be truncated by a later checkpoint.",
                    databasePath);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception: ex,
                message: "The one-time scrub of {DatabasePath} failed and will be retried at the next start.",
                databasePath);
            return Outcome.Deferred;
        }

        WriteMarker(markerPath);
        logger.LogInformation("The one-time scrub of {DatabasePath} completed.", databasePath);
        return Outcome.Completed;
    }

    /// <summary>Records that the scrub is done for a database, creating the marker's directory if needed.</summary>
    /// <param name="markerPath">The marker file to write.</param>
    internal static void WriteMarker(string markerPath)
    {
        var directory = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path: markerPath, contents: DateTimeOffset.UtcNow.ToString("O"));
    }

    private static VolumeSpace ProbeVolume(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new InvalidOperationException("No volume root.");
        var drive = new DriveInfo(root);
        return new VolumeSpace(Free: drive.AvailableFreeSpace, Total: drive.TotalSize);
    }

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
