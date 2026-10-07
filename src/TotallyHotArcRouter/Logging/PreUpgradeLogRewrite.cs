using ILogger = Serilog.ILogger;

namespace TotallyHot.ArcRouter.Logging;

/// <summary>
/// One-time rewrite of pre-upgrade diagnostic logs that still hold unobscured F9 conversation excerpts
/// (#184 plan §3.3). Runs only while the router is stopped: from
/// <c>--migrate-data-directory</c>, and from the Docker bootstrap before the host File sink opens any
/// file. Never at service start, because launchd may already hold <c>launchd-stdout.log</c> open.
/// </summary>
public static class PreUpgradeLogRewrite
{
    /// <summary>
    /// Marker written into the protected data root once the rewrite has finished (or found nothing to
    /// do). Prevents a second pass on later upgrades.
    /// </summary>
    public const string MarkerFileName = ".pre-upgrade-logs-rewritten";

    private static readonly string[] FileNamePatterns =
    [
        "arcrouter-*.log",
        "launchd-stdout.log",
        "launchd-stderr.log"
    ];

    /// <summary>
    /// Rewrites every matching log under <paramref name="logsDirectory"/> when the marker is not yet
    /// present in <paramref name="dataDirectory"/>, then writes the marker. Idempotent. The marker is only
    /// written when every file was rewritten or deleted; a file that could be neither is retried on the next
    /// run instead of being left behind for good.
    /// </summary>
    /// <param name="dataDirectory">Protected data root that holds the marker.</param>
    /// <param name="logsDirectory">Directory that holds <c>arcrouter-*.log</c> (and macOS launchd copies).</param>
    /// <param name="logger">Receives one line per rewritten or deleted file.</param>
    public static void Run(string dataDirectory, string logsDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);

        Run(dataDirectory, [logsDirectory], logger);
    }

    /// <summary>
    /// Same as the single-directory overload for installs whose logs may live in more than one place (Linux
    /// keeps them outside the state directory, yet a manual run writes under it). One marker covers all of
    /// them, written only when every directory was cleaned.
    /// </summary>
    /// <param name="dataDirectory">Protected data root that holds the marker.</param>
    /// <param name="logsDirectories">Directories that may hold <c>arcrouter-*.log</c>; missing ones are skipped.</param>
    /// <param name="logger">Receives one line per rewritten or deleted file.</param>
    public static void Run(string dataDirectory, IEnumerable<string> logsDirectories, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logsDirectories);
        ArgumentNullException.ThrowIfNull(logger);

        var markerPath = Path.Combine(dataDirectory, MarkerFileName);
        if (File.Exists(markerPath)) return;

        var allHandled = true;
        foreach (var logsDirectory in logsDirectories)
        {
            if (!Directory.Exists(logsDirectory)) continue;

            foreach (var pattern in FileNamePatterns)
            {
                foreach (var path in Directory.EnumerateFiles(logsDirectory, pattern))
                    allHandled &= RewriteOrDelete(path, logger);
            }
        }

        if (!allHandled)
        {
            logger.Warning(
                "Left the pre-upgrade log rewrite marker unwritten because a log file could not be cleaned; the next run retries.");
            return;
        }

        TryRecordMarker(dataDirectory, markerPath, logger);
    }

    /// <summary>
    /// Writes the marker, or logs a one-line reason when the data root cannot be written. This is a
    /// best-effort cleanup that runs on the startup path, so a read-only root must not stop the router or
    /// dump a stack trace; the only cost is that the next run repeats the (idempotent) rewrite.
    /// </summary>
    private static void TryRecordMarker(string dataDirectory, string markerPath, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            using (File.Create(markerPath))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error(
                "Could not record the pre-upgrade log rewrite marker at {MarkerPath}: {Reason} The old logs were cleaned, and the router will check them again at the next start.",
                markerPath, ex.Message);
            return;
        }

        logger.Information(
            "Recorded the pre-upgrade log rewrite marker at {MarkerPath}.",
            markerPath);
    }

    /// <summary>
    /// Filters <paramref name="path"/> through a temp copy and writes the result back into the same file.
    /// Falls back to deleting the file when it cannot be rewritten.
    /// </summary>
    /// <returns>Whether the file no longer holds conversation text.</returns>
    private static bool RewriteOrDelete(string path, ILogger logger)
    {
        var tempPath = path + ".rewriting";
        try
        {
            using (var reader = new StreamReader(path))
            using (var writer = new StreamWriter(tempPath))
            {
                while (reader.ReadLine() is { } line)
                {
                    if (IsConversationBodyLine(line)) continue;
                    writer.WriteLine(line);
                }
            }

            // Copy back into the original file instead of moving the temp over it: the original keeps its
            // owner, mode, and ACL, so a root-run migration cannot leave the service account unable to
            // append to today's log.
            using (var source = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var target = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                target.SetLength(0);
                source.CopyTo(target);
            }

            File.Delete(tempPath);
            logger.Information("Rewrote {LogPath} to remove pre-upgrade conversation body lines.", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
            }

            try
            {
                File.Delete(path);
                logger.Warning(ex,
                    "Could not rewrite {LogPath}; deleted it instead so the unobscured excerpts do not linger.",
                    path);
                return true;
            }
            catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
            {
                logger.Warning(deleteEx,
                    "Could not rewrite or delete {LogPath}; an administrator should remove it.",
                    path);
                return false;
            }
        }
    }

    /// <summary>Returns whether <paramref name="line"/> is one of the four F9 conversation-bearing templates.</summary>
    internal static bool IsConversationBodyLine(string line)
    {
        foreach (var prefix in ConversationBodyLogging.LegacyMessagePrefixes)
        {
            if (line.Contains(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
