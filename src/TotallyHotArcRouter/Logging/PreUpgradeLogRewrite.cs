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
    /// present in <paramref name="dataDirectory"/>, then writes the marker. Idempotent.
    /// </summary>
    /// <param name="dataDirectory">Protected data root that holds the marker.</param>
    /// <param name="logsDirectory">Directory that holds <c>arcrouter-*.log</c> (and macOS launchd copies).</param>
    /// <param name="logger">Receives one line per rewritten or deleted file.</param>
    public static void Run(string dataDirectory, string logsDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var markerPath = Path.Combine(dataDirectory, MarkerFileName);
        if (File.Exists(markerPath)) return;

        Directory.CreateDirectory(dataDirectory);

        if (Directory.Exists(logsDirectory))
        {
            foreach (var pattern in FileNamePatterns)
            {
                foreach (var path in Directory.EnumerateFiles(logsDirectory, pattern))
                    RewriteOrDelete(path, logger);
            }
        }

        using (File.Create(markerPath))
        {
        }

        logger.Information(
            "Recorded the pre-upgrade log rewrite marker at {MarkerPath}.",
            markerPath);
    }

    private static void RewriteOrDelete(string path, ILogger logger)
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

            File.Move(tempPath, path, overwrite: true);
            logger.Information("Rewrote {LogPath} to remove pre-upgrade conversation body lines.", path);
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
            }
            catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
            {
                logger.Warning(deleteEx,
                    "Could not rewrite or delete {LogPath}; an administrator should remove it.",
                    path);
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
