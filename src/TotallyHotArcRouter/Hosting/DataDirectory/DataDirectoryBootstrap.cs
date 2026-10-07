using System.Runtime.Versioning;
using Serilog;
using TotallyHot.ArcRouter.Logging;
using ILogger = Serilog.ILogger;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// What <see cref="DataDirectoryBootstrap"/> decided for one process: the directory to use, how it relates
/// to the machine-wide one, and the log lines to write once logging exists.
/// </summary>
/// <param name="Directory">The directory this process uses.</param>
/// <param name="UsingProtectedMachineWide">Whether <paramref name="Directory"/> is the protected machine-wide directory.</param>
/// <param name="MachineWideUnavailable">Whether the machine-wide directory exists but this process could not use it.</param>
/// <param name="Events">Log lines to write later, each with a static template.</param>
public sealed record DataDirectoryResolution(
    string Directory,
    bool UsingProtectedMachineWide,
    bool MachineWideUnavailable,
    IReadOnlyList<Action<ILogger>> Events);

/// <summary>
/// Decides, before anything else touches it, whether this process may use the machine-wide data
/// directory (ADR-0024 rule 2). It runs inside <see cref="AppDataPaths.ResolveMachineSharedDirectory"/>,
/// ahead of that method's write probe, and so ahead of <c>Program.CreateHostBuilder</c> registering
/// <c>appsettings.local.json</c> from the same directory - a planted overlay is never loaded.
/// </summary>
/// <remarks>
/// <para>The outcome depends on the directory's state and on who is asking:</para>
/// <list type="bullet">
/// <item><description>
/// <b>Protected</b>: used, if this process can write into it. Otherwise this process is not the service
/// or an administrator, and it falls back to the per-user directory, as an unelevated dev run already
/// did when ADR-0015's admin-only secret store refused it.
/// </description></item>
/// <item><description>
/// <b>Missing</b>: an elevated process (or, on Linux and macOS, any process that can create it) creates
/// it protected, atomically. An unelevated Windows process falls back instead of creating an
/// unprotected directory that the service would later refuse.
/// </description></item>
/// <item><description>
/// <b>Unprotected</b>: the service - an elevated Windows process, or the Unix account that owns the
/// directory - fails closed with <see cref="DataDirectoryNotProtectedException"/>, naming
/// <c>--migrate-data-directory</c>. It never re-creates an empty directory over a legacy one. Another
/// process falls back. The one exception is a container (<see cref="ContainerEnvironmentVariable"/>),
/// whose volume is migrated in place here, as plan §3.1 describes, and which never falls back to a
/// directory that would vanish with the container.
/// </description></item>
/// </list>
/// <para>
/// The host's logger does not exist yet when this runs, so outcomes are buffered and written by
/// <see cref="FlushPendingEvents"/> once logging is configured, with static templates.
/// </para>
/// </remarks>
public static class DataDirectoryBootstrap
{
    /// <summary>
    /// Set to <c>1</c> by the router's container image. It permits the in-place migration of an older
    /// named volume, which is safe only inside a container (see <see cref="ContainerDataDirectoryMigration"/>).
    /// </summary>
    public const string ContainerEnvironmentVariable = "TOTALLYHOT_ARCROUTER_CONTAINER";

    /// <summary>
    /// Overrides the Linux or macOS service account's name (default <c>arcrouter</c> on Linux and
    /// <c>_arcrouter</c> on macOS) for a root-run process deciding whether it may use the service's
    /// directory, and for <c>--migrate-data-directory</c>.
    /// </summary>
    public const string ServiceAccountEnvironmentVariable = "ARCROUTER_SERVICE_ACCOUNT";

    private const string InterruptedMigrationReason =
        "a migration was interrupted before its new tree was moved into place";

    private static readonly Lock Gate = new();
    private static readonly List<Action<ILogger>> PendingEvents = [];

    /// <summary>
    /// Whether this process found the service's data directory but could not use it, and fell back to the
    /// per-user directory. Commands that read the service's state - the management token, the local CA -
    /// check this and ask to be run elevated, rather than silently printing a different, per-user value.
    /// </summary>
    public static bool MachineWideDirectoryUnavailable { get; private set; }

    /// <summary>
    /// Whether the directory this process resolved is the protected, machine-wide one. The secret store
    /// writes its machine-wide ACL only there; anywhere else it keeps a current-user-only ACL, which an
    /// unelevated writer cannot lock itself out of.
    /// </summary>
    public static bool UsingProtectedMachineWideDirectory { get; private set; }

    /// <summary>The default service account name on this platform.</summary>
    public static string DefaultServiceAccountName => OperatingSystem.IsMacOS() ? "_arcrouter" : "arcrouter";

    /// <summary>
    /// Returns the directory this process should use - <paramref name="machineWide"/> when it passes the
    /// checks above, otherwise <paramref name="perUser"/>, otherwise <paramref name="lastResort"/> - and
    /// records the decision for <see cref="MachineWideDirectoryUnavailable"/>,
    /// <see cref="UsingProtectedMachineWideDirectory"/> and <see cref="FlushPendingEvents"/>.
    /// </summary>
    /// <exception cref="DataDirectoryNotProtectedException">This process is the service, and the machine-wide directory is not protected.</exception>
    internal static string Resolve(string machineWide, string perUser, string lastResort)
    {
        var resolution = OperatingSystem.IsWindows()
            ? DecideWindows(machineWide, perUser, lastResort, WindowsDirectoryPolicy.Machine,
                WindowsDataDirectorySecurity.IsElevated())
            : DecideUnix(machineWide, perUser, lastResort, TrustedUnixOwners(),
                Environment.GetEnvironmentVariable(ContainerEnvironmentVariable) == "1",
                ResolveServiceAccountUid());

        UsingProtectedMachineWideDirectory = resolution.UsingProtectedMachineWide;
        MachineWideDirectoryUnavailable = resolution.MachineWideUnavailable;
        lock (Gate)
        {
            PendingEvents.AddRange(resolution.Events);
        }

        // #184 phase 3: Docker has no elevated --migrate-data-directory step, so the bootstrap itself
        // rewrites pre-upgrade logs before the host File sink opens anything. Non-container starts never
        // rewrite here - launchd may already hold launchd-stdout.log open.
        if (resolution.UsingProtectedMachineWide &&
            Environment.GetEnvironmentVariable(ContainerEnvironmentVariable) == "1")
        {
            PreUpgradeLogRewrite.Run(
                dataDirectory: resolution.Directory,
                logsDirectories: DataDirectoryMigrationCommand.PreUpgradeLogDirectories(resolution.Directory),
                logger: Log.Logger);
        }

        return resolution.Directory;
    }

    /// <summary>
    /// The Windows decision, with the policy and elevation passed in so tests can run it against temp
    /// directories with the real ACL code. <see cref="Resolve"/> passes
    /// <see cref="WindowsDirectoryPolicy.Machine"/> and the process's real elevation.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static DataDirectoryResolution DecideWindows(string machineWide, string perUser, string lastResort,
        WindowsDirectoryPolicy policy, bool elevated)
    {
        List<Action<ILogger>> events = [];
        var inspection = WindowsDataDirectorySecurity.Inspect(machineWide, policy);

        switch (inspection.State)
        {
            case DataDirectoryState.Protected when AppDataPaths.TryEnsureDirectory(machineWide):
                return UseMachineWide(machineWide, events);

            case DataDirectoryState.Missing when DataDirectoryMigrationRules.FindStaging(machineWide).Count > 0:
                // A crash landed between migration's two renames. Never create an empty root here: the
                // operator's data is in the staging tree, and the next elevated migration finishes the swap.
                if (elevated) throw new DataDirectoryNotProtectedException(machineWide, InterruptedMigrationReason);
                return FallBack(machineWide, perUser, lastResort, unavailable: true, events);

            case DataDirectoryState.Missing when elevated:
                WindowsDataDirectorySecurity.CreateProtected(machineWide, policy);
                events.Add(logger => logger.Information("Created the protected data directory {Root}.", machineWide));
                return UseMachineWide(machineWide, events);

            case DataDirectoryState.Missing:
                return FallBack(machineWide, perUser, lastResort, unavailable: false, events);

            case DataDirectoryState.Unprotected when elevated:
                throw new DataDirectoryNotProtectedException(machineWide, inspection.Reason ?? "it failed inspection");

            // An elevated process is the service or an administrator acting for it. Falling back here would
            // start it against SYSTEM's own per-user profile directory - split state - rather than refusing a
            // service directory it should be able to use but cannot.
            case DataDirectoryState.Protected when elevated:
                throw new DataDirectoryNotProtectedException(machineWide,
                    "it is protected, but this elevated process cannot write to it");

            case DataDirectoryState.Inaccessible when elevated:
                throw new DataDirectoryNotProtectedException(machineWide,
                    "this elevated process cannot read its permissions");

            default:
                return FallBack(machineWide, perUser, lastResort, unavailable: true, events);
        }
    }

    /// <summary>
    /// The Linux and macOS decision, with the trusted owners and container mode passed in so tests can run
    /// it. <see cref="Resolve"/> passes this process's own uid (plus the service account's when it runs as
    /// root) and <see cref="ContainerEnvironmentVariable"/>.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    internal static DataDirectoryResolution DecideUnix(string machineWide, string perUser, string lastResort,
        IReadOnlyCollection<uint> trustedOwners, bool container, uint? serviceAccountUid = null)
    {
        List<Action<ILogger>> events = [];
        var inspection = UnixDataDirectorySecurity.Inspect(machineWide, trustedOwners);

        // The service account, like a container, never falls back: its per-user directory would give the
        // installed service a second, split copy of its state. Any other account still may.
        var mustNotFallBack = container ||
                              (serviceAccountUid is { } serviceUid && UnixNative.EffectiveUserId() == serviceUid);

        if (inspection.State == DataDirectoryState.Protected && UnixDataDirectoryMigration.HasMarker(machineWide))
        {
            if (AppDataPaths.TryEnsureDirectory(machineWide)) return UseMachineWide(machineWide, events);
            if (mustNotFallBack)
                throw new DataDirectoryNotProtectedException(machineWide,
                    "it is protected, but the router cannot write to it (a read-only filesystem or an ACL denying writes?)");
            return FallBack(machineWide, perUser, lastResort, unavailable: true, events);
        }

        if (inspection.State == DataDirectoryState.Missing)
        {
            var euid = UnixNative.EffectiveUserId();
            if (DataDirectoryMigrationRules.FindStaging(machineWide)
                .Any(staging => euid == 0 || UnixNative.LStat(staging)?.Uid == euid))
                throw new DataDirectoryNotProtectedException(machineWide, InterruptedMigrationReason);

            try
            {
                UnixDataDirectorySecurity.CreateProtected(machineWide);
                using (File.Create(Path.Combine(machineWide, DataDirectoryMigrationRules.ProtectedMarkerFileName)))
                {
                }

                events.Add(logger => logger.Information("Created the protected data directory {Root}.", machineWide));
                return UseMachineWide(machineWide, events);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (mustNotFallBack) throw new DataDirectoryNotProtectedException(machineWide, "the router cannot create it");
                return FallBack(machineWide, perUser, lastResort, unavailable: false, events);
            }
        }

        // Unprotected, or protected by mode but never migrated. When this process owns the directory it is
        // the service (or root acting for it), and using the directory as it is would defeat the point.
        if (inspection is { State: DataDirectoryState.Unprotected or DataDirectoryState.Protected, OwnerTrusted: true })
        {
            if (!container)
                throw new DataDirectoryNotProtectedException(machineWide,
                    inspection.Reason ?? "it has not been migrated yet");

            new ContainerDataDirectoryMigration(machineWide, Log.Logger).Run();
            events.Add(logger => logger.Information("Migrated the container data volume {Root} in place.", machineWide));
            return UseMachineWide(machineWide, events);
        }

        // A container must never fall back: its per-user and last-resort directories vanish with the
        // container, so the router would silently start with empty state on every run.
        if (container)
            throw new DataDirectoryNotProtectedException(machineWide,
                inspection.Reason ?? "the router cannot use it; a bind mount must be owned by the container's account with mode 0700");

        // The service account refuses any root it cannot use. The common case is a blocked migration, which
        // leaves the old tree root-owned and 0700; on macOS nothing else would stop a fallback.
        if (mustNotFallBack)
            throw new DataDirectoryNotProtectedException(machineWide,
                UnixNative.LStat(machineWide) is { Kind: UnixFileKind.Directory, Uid: 0 }
                    ? "an interrupted or blocked migration left it locked to root"
                    : inspection.Reason ?? "the service account cannot use it");

        return FallBack(machineWide, perUser, lastResort, unavailable: true, events);
    }

    /// <summary>
    /// Verifies a separate logs directory - Linux's <c>LOGS_DIRECTORY</c>, which lives outside the state
    /// directory - with the same rules as the state directory, creating it owner-only when missing.
    /// </summary>
    /// <exception cref="DataDirectoryNotProtectedException">The directory exists but is not protected.</exception>
    [UnsupportedOSPlatform("windows")]
    internal static void VerifyUnixLogsDirectory(string logsDirectory)
    {
        var inspection = UnixDataDirectorySecurity.Inspect(logsDirectory, TrustedUnixOwners());
        switch (inspection.State)
        {
            case DataDirectoryState.Missing:
                // With the marker, as DecideUnix does for a new state directory; without it the next start
                // would see a 0700 directory that was never marked and refuse it.
                UnixDataDirectorySecurity.CreateProtected(logsDirectory);
                using (File.Create(Path.Combine(logsDirectory, DataDirectoryMigrationRules.ProtectedMarkerFileName)))
                {
                }

                return;
            case DataDirectoryState.Protected when UnixDataDirectoryMigration.HasMarker(logsDirectory):
                return;
            case DataDirectoryState.Protected:
                throw new DataDirectoryNotProtectedException(logsDirectory, "it has not been migrated yet");
            default:
                throw new DataDirectoryNotProtectedException(logsDirectory,
                    inspection.Reason ?? "it could not be inspected");
        }
    }

    /// <summary>
    /// After a root-run command against the service's directory, hands every entry root created there back to
    /// the directory's owner, the service account. Without this, a manual <c>sudo ... --print-management-token</c>
    /// or <c>--export-ca</c> that had to create a missing secret or certificate would leave a root-owned
    /// <c>0600</c> file the service cannot read. A no-op on Windows, for any other user, and when this process
    /// is not using the protected machine-wide directory.
    /// </summary>
    public static void RestoreServiceOwnership(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (OperatingSystem.IsWindows() || !UsingProtectedMachineWideDirectory ||
            UnixNative.EffectiveUserId() != 0)
            return;

        var restored = RestoreServiceOwnership(AppDataPaths.ResolveMachineSharedDirectory());
        if (restored > 0)
            logger.Information("Handed {Count} root-created entr(ies) back to the service account.", restored);
    }

    /// <summary>
    /// Changes every root-owned entry under <paramref name="root"/> to <paramref name="root"/>'s own owner,
    /// without following links, and returns how many it changed. Safe to walk by path: the tree is
    /// <c>0700</c>, so only root and the service account can change its entries.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    internal static int RestoreServiceOwnership(string root)
    {
        if (UnixNative.LStat(root) is not { Kind: UnixFileKind.Directory, Uid: not 0 } owner) return 0;

        var restored = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).ToList())
        {
            if (UnixNative.LStat(entry) is not { } status) continue;

            // Never touch a nested mount, not even its root: that would chown files outside the service's tree.
            if (status.Kind == UnixFileKind.Directory && !status.IsOnSameMountAs(owner)) continue;

            if (status.Uid == 0)
            {
                UnixNative.LChown(entry, owner.Uid, owner.Gid);
                restored++;
            }

            if (status.Kind == UnixFileKind.Directory) restored += RestoreServiceOwnership(entry);
        }

        return restored;
    }

    /// <summary>
    /// Writes the outcomes buffered since startup to <paramref name="logger"/>, then forgets them. Called
    /// once the host's logger exists, and by the command-line paths that never build a host.
    /// </summary>
    public static void FlushPendingEvents(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        List<Action<ILogger>> events;
        lock (Gate)
        {
            events = [.. PendingEvents];
            PendingEvents.Clear();
        }

        foreach (var write in events) write(logger);
    }

    private static DataDirectoryResolution UseMachineWide(string machineWide, List<Action<ILogger>> events)
    {
        foreach (var leftover in DataDirectoryMigrationRules.FindRetired(machineWide)
                     .Concat(DataDirectoryMigrationRules.FindStaging(machineWide)))
            events.Add(logger => logger.Warning(
                "{Leftover} was left behind by an earlier data-directory migration. Run --migrate-data-directory elevated to finish cleaning it up.",
                leftover));

        return new DataDirectoryResolution(machineWide, UsingProtectedMachineWide: true, MachineWideUnavailable: false,
            events);
    }

    private static DataDirectoryResolution FallBack(string machineWide, string perUser, string lastResort,
        bool unavailable, List<Action<ILogger>> events)
    {
        var chosen = AppDataPaths.TryEnsureDirectory(perUser) ? perUser : lastResort;
        if (chosen == lastResort) Directory.CreateDirectory(lastResort);

        if (!OperatingSystem.IsWindows()) TightenPerUserDirectory(chosen);

        if (unavailable)
            events.Add(logger => logger.Information(
                "Using the per-user data directory {Chosen}: this process cannot use the protected machine-wide directory {MachineWide}. Run elevated to use the service's data.",
                chosen, machineWide));

        return new DataDirectoryResolution(chosen, UsingProtectedMachineWide: false, MachineWideUnavailable: unavailable,
            events);
    }

    /// <summary>
    /// Drops group and other bits from a per-user directory this process owns. The per-user fallback
    /// cannot meet ADR-0024's requirement - every application the user runs can read it - but there is no
    /// reason to leave it readable by other accounts as well.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static void TightenPerUserDirectory(string directory)
    {
        try
        {
            if (UnixNative.LStat(directory) is { Kind: UnixFileKind.Directory } status &&
                status.Uid == UnixNative.EffectiveUserId() && status.GrantsGroupOrOther)
                File.SetUnixFileMode(directory, UnixDataDirectorySecurity.OwnerOnlyDirectoryMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Best effort: a directory this process cannot re-mode is one it does not own.
        }
    }

    /// <summary>The configured service account's uid, or <see langword="null"/> when it does not exist.</summary>
    [UnsupportedOSPlatform("windows")]
    private static uint? ResolveServiceAccountUid()
    {
        var accountName = Environment.GetEnvironmentVariable(ServiceAccountEnvironmentVariable);
        return UnixNative.TryResolveAccount(string.IsNullOrWhiteSpace(accountName)
            ? DefaultServiceAccountName
            : accountName)?.Uid;
    }

    /// <summary>
    /// The uids a Unix data directory may be owned by from this process's point of view: its own, and
    /// when running as root, the service account's too (install scripts run some flags as root against
    /// the service's directory).
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    internal static uint[] TrustedUnixOwners()
    {
        var euid = UnixNative.EffectiveUserId();
        if (euid != 0) return [euid];

        var accountName = Environment.GetEnvironmentVariable(ServiceAccountEnvironmentVariable);
        var service = UnixNative.TryResolveAccount(string.IsNullOrWhiteSpace(accountName)
            ? DefaultServiceAccountName
            : accountName);
        return service is { } account ? [0, account.Uid] : [0];
    }
}

/// <summary>
/// Thrown at startup when the service finds its data directory unprotected (ADR-0024). The service
/// refuses to start rather than use - or replace - the directory; the message names the command that
/// fixes it.
/// </summary>
public sealed class DataDirectoryNotProtectedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="DataDirectoryNotProtectedException"/> class.</summary>
    /// <param name="path">The directory that failed verification.</param>
    /// <param name="reason">Why, in words.</param>
    public DataDirectoryNotProtectedException(string path, string reason)
        : base(
            $"{Describe(path, reason)} Run 'TotallyHotArcRouter --migrate-data-directory' as an administrator (as root on Linux and macOS), then start the router again.")
    {
        Path = path;
        Reason = reason;
    }

    /// <summary>The directory that failed verification.</summary>
    public string Path { get; }

    /// <summary>Why the directory failed verification, in words, as passed to the constructor.</summary>
    public string Reason { get; }

    /// <summary>
    /// The sentence stating what failed, without the remedy. The remedy is added separately wherever the
    /// exception is reported: <see cref="Exception.Message"/> appends a generic one, and
    /// <see cref="DataDirectoryAdvice"/> supplies the exact command for the launched process.
    /// </summary>
    /// <param name="path">The directory that failed verification.</param>
    /// <param name="reason">Why, in words.</param>
    internal static string Describe(string path, string reason) =>
        $"The data directory '{path}' is not protected ({reason}), so the router will not use it.";
}
