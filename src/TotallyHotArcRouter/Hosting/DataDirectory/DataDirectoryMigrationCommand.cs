using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using TotallyHot.ArcRouter.Logging;
using ILogger = Serilog.ILogger;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// The router's <c>--migrate-data-directory</c> command (ADR-0024 rule 3): creates, migrates or repairs
/// the machine-wide data directory so it is protected, and is safe to run repeatedly. The MSI runs it
/// (as <c>SYSTEM</c>) before it starts the service on every install and upgrade, and <c>install.sh</c> runs
/// it as root on Linux and macOS with the service stopped. An operator runs it by hand from an elevated
/// prompt when the service refuses to start.
/// </summary>
/// <remarks>
/// <para>Optional arguments:</para>
/// <list type="bullet">
/// <item><description>
/// <c>--legacy-owner-sid=&lt;SID&gt;</c> (Windows): the account that installed the router. The MSI passes
/// its <c>UserSID</c>, because the custom action itself runs as <c>SYSTEM</c>. Defaults to the account
/// running the command.
/// </description></item>
/// <item><description>
/// <c>--service-account=&lt;name&gt;</c> (Linux and macOS): the account the new tree is owned by. Defaults
/// to <see cref="DataDirectoryBootstrap.ServiceAccountEnvironmentVariable"/>, then
/// <see cref="DataDirectoryBootstrap.DefaultServiceAccountName"/>. The legacy owner there is the
/// <c>sudo</c> caller (<c>SUDO_UID</c>), never root itself.
/// </description></item>
/// </list>
/// <para>
/// It resolves its paths with <see cref="AppDataPaths.MachineWideCandidate"/>, never through
/// <see cref="AppDataPaths.ResolveMachineSharedDirectory"/>, whose bootstrap would refuse the very
/// directory this command exists to fix.
/// </para>
/// </remarks>
public static class DataDirectoryMigrationCommand
{
    /// <summary>The command-line flag that runs this command.</summary>
    public const string FlagName = "--migrate-data-directory";

    private const string LegacyOwnerSidPrefix = "--legacy-owner-sid=";
    private const string ServiceAccountPrefix = "--service-account=";
    private const string DefaultLinuxLogsDirectory = "/var/log/totallyhot-arcrouter";
    private const string EventLogSource = "TotallyHotArcRouter";

    /// <summary>
    /// Runs the command and returns the process exit code: 0 on success, 1 when migration was blocked or
    /// failed, 2 when the command was not run with the rights it needs or got a bad argument.
    /// </summary>
    /// <param name="args">The arguments that followed the flag.</param>
    /// <param name="logger">Receives the command's progress and outcome.</param>
    public static int Run(IReadOnlyList<string> args, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return OperatingSystem.IsWindows() ? RunWindows(args, logger) : RunUnix(args, logger);
        }
        catch (DataDirectoryMigrationBlockedException ex)
        {
            logger.Error(ex, "Data-directory migration stopped before finishing: {Reason}", ex.Message);
            ReportToEventLog($"Data-directory migration stopped before finishing: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception)
        {
            logger.Error(ex, "Data-directory migration failed.");
            ReportToEventLog($"Data-directory migration failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Writes <paramref name="message"/> to the Windows Application event log under the router's source,
    /// best effort. A failed service start or migration otherwise leaves no trace an operator can find:
    /// the service has no console, and the file log lives in the directory that just failed verification.
    /// A no-op off Windows, where systemd's journal and launchd's log already capture stderr.
    /// </summary>
    public static void ReportToEventLog(string message)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (!EventLog.SourceExists(EventLogSource))
                EventLog.CreateEventSource(source: EventLogSource, logName: "Application");
            EventLog.WriteEntry(source: EventLogSource, message: message, type: EventLogEntryType.Error);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or ArgumentException)
        {
            // Best effort: an unelevated process cannot register the source, and has a console anyway.
        }
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(IReadOnlyList<string> args, ILogger logger)
    {
        if (!WindowsDataDirectorySecurity.IsElevated())
        {
            logger.Error("{Flag} must be run from an elevated prompt.", FlagName);
            return 2;
        }

        SecurityIdentifier? legacyOwner;
        var sidArgument = ValueOf(args, LegacyOwnerSidPrefix);
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            legacyOwner = string.IsNullOrWhiteSpace(sidArgument) ? current.User : new SecurityIdentifier(sidArgument);
        }
        catch (ArgumentException)
        {
            logger.Error("{Argument} is not a valid SID.", sidArgument);
            return 2;
        }

        var root = AppDataPaths.MachineWideCandidate();
        var result = new WindowsDataDirectoryMigration(root, WindowsDirectoryPolicy.Machine, legacyOwner, logger).Run();
        LogResult(logger, root, result);
        RewritePreUpgradeLogs(root, logger);
        return 0;
    }

    [UnsupportedOSPlatform("windows")]
    private static int RunUnix(IReadOnlyList<string> args, ILogger logger)
    {
        if (UnixNative.EffectiveUserId() != 0)
        {
            logger.Error("{Flag} must be run as root.", FlagName);
            return 2;
        }

        var accountName = ValueOf(args, ServiceAccountPrefix) ??
                          Environment.GetEnvironmentVariable(DataDirectoryBootstrap.ServiceAccountEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(accountName)) accountName = DataDirectoryBootstrap.DefaultServiceAccountName;

        if (UnixNative.TryResolveAccount(accountName) is not { } account)
        {
            logger.Error("The service account {Account} does not exist; create it before running {Flag}.", accountName,
                FlagName);
            return 2;
        }

        uint? legacyUid = uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var sudoUid) && sudoUid != 0
            ? sudoUid
            : null;

        IOpenWriterScanner scanner = OperatingSystem.IsMacOS() ? new MacOpenWriterScanner() : new LinuxOpenWriterScanner();

        List<string> roots = [AppDataPaths.MachineWideCandidate()];
        if (OperatingSystem.IsLinux())
        {
            var logsDirectory = Environment.GetEnvironmentVariable("LOGS_DIRECTORY");
            roots.Add(string.IsNullOrWhiteSpace(logsDirectory)
                ? DefaultLinuxLogsDirectory
                : logsDirectory.Split(':', 2)[0]);
        }

        foreach (var root in roots)
        {
            var result = new UnixDataDirectoryMigration(root, account.Uid, account.Gid, legacyUid, scanner, logger).Run();
            LogResult(logger, root, result);
        }

        RewritePreUpgradeLogs(AppDataPaths.MachineWideCandidate(), logger);
        return 0;
    }

    /// <summary>
    /// Strips legacy F9 conversation lines from diagnostic logs while the router is stopped (#184 phase 3).
    /// Uses <see cref="AppDataPaths.MachineWideCandidate"/> rather than
    /// <see cref="AppDataPaths.ResolveLogsDirectory"/> so it never re-enters the bootstrap that refuses an
    /// unmigrated root.
    /// </summary>
    private static void RewritePreUpgradeLogs(string dataDirectory, ILogger logger)
    {
        PreUpgradeLogRewrite.Run(dataDirectory, ResolveLogsDirectoryForMigration(), logger);
    }

    private static string ResolveLogsDirectoryForMigration()
    {
        var logsDirectory = Environment.GetEnvironmentVariable("LOGS_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(logsDirectory))
            return logsDirectory.Split(':', 2)[0];

        return Path.Combine(AppDataPaths.MachineWideCandidate(), "logs");
    }

    private static void LogResult(ILogger logger, string root, DataDirectoryMigrationResult result)
    {
        logger.Information(
            "Data directory {Root}: {Outcome}; {Adopted} file(s) adopted, {Quarantined} kept out, {Discarded} discarded.",
            root, result.Outcome, result.Adopted, result.Quarantined, result.Discarded);

        if (result.QuarantinePath is not null)
            logger.Warning(
                "Files kept out of the live directory are in {QuarantinePath}. Review them from an elevated prompt; to keep appsettings.local.json, copy it back into {Root} yourself.",
                result.QuarantinePath, root);
    }

    private static string? ValueOf(IReadOnlyList<string> args, string prefix)
    {
        return args.Where(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(arg => arg[prefix.Length..])
            .LastOrDefault();
    }
}
