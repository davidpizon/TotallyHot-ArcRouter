namespace TotallyHot.ArcRouter.Storage;

/// <summary>
/// The router's internal <c>--scrub-database</c> command (#184, ADR-0024): the child half of
/// <see cref="ScrubProcessLauncher"/>. It runs <see cref="SqliteScrub"/> on the database it is given and
/// reports through its exit code. It is dispatched from <c>Program.Main</c> before any host or data
/// directory is built, and touches nothing but the two paths it is handed.
/// </summary>
/// <remarks>
/// Running it by hand can only rebuild a SQLite file the caller already has write access to, with the
/// caller's own rights, so it grants nothing the caller did not have.
/// </remarks>
internal static class ScrubDatabaseCommand
{
    /// <summary>The command-line flag that selects this command.</summary>
    internal const string FlagName = "--scrub-database";

    /// <summary>The exit code for a scrub that finished or was not needed.</summary>
    internal const int DoneExitCode = 0;

    /// <summary>The exit code for a scrub that was deferred or failed, to be retried at the next start.</summary>
    internal const int DeferredExitCode = 3;

    /// <summary>The exit code for malformed arguments.</summary>
    internal const int UsageExitCode = 2;

    /// <summary>
    /// Runs the scrub for the arguments left after the flag was removed.
    /// </summary>
    /// <param name="args">The database path, then the marker path.</param>
    /// <param name="logger">Receives the scrub's log lines; the parent forwards the child's console output.</param>
    /// <returns>An exit code: <see cref="DoneExitCode"/>, <see cref="DeferredExitCode"/> or <see cref="UsageExitCode"/>.</returns>
    internal static int Run(string[] args, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(logger);

        if (args.Length != 2)
        {
            logger.LogError("{Flag} takes exactly two arguments: the database path and the marker path.", FlagName);
            return UsageExitCode;
        }

        var outcome = SqliteScrub.Run(databasePath: args[0], markerPath: args[1], logger: logger);
        return outcome == SqliteScrub.Outcome.Deferred ? DeferredExitCode : DoneExitCode;
    }
}
