namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Resolves the one machine-shared data directory every piece of router state persists under - the
/// management token, the routing-gate file, the price-catalog database, the protected secret store, the
/// telemetry TLS certificate, and the local-model caches. Introduced by
/// <see href="../../../docs/gui/web-gui-migration-plan.md">the web GUI migration plan</see>'s Phase P3 to
/// replace three independent, subtly different resolvers this router had accumulated over time
/// (<c>StorageOptions.MachineSharedRoot</c>, <c>RoutingGateStore.DefaultPath</c>, and
/// <c>ManagementAccessToken.DefaultPath</c> each duplicated a slightly different version of "where does
/// machine-wide state live"), and to give Linux/macOS a real answer instead of the per-user fallback
/// <c>StorageOptions</c> used as a stand-in until this resolver existed.
/// </summary>
public static class AppDataPaths
{
    /// <summary>The directory name this application's data lives under, on every platform.</summary>
    public const string ApplicationDirectoryName = "TotallyHotArcRouter";

    // Memoized: resolution performs a one-time directory-creation probe (see ResolveMachineSharedDirectory's
    // remarks) whose answer cannot meaningfully change within one process's lifetime - environment
    // variables and file permissions are read once at startup in practice, not toggled mid-run.
    private static string? _cachedDirectory;

    /// <summary>
    /// Resolves the machine-shared data directory for this platform, preferring a true machine-wide
    /// location and falling back to a per-user one only when the machine-wide candidate can't be created
    /// or written to - the common case for an unprivileged developer running the router directly, with no
    /// dedicated service account and no elevation. The result is memoized for the process's lifetime.
    /// </summary>
    /// <remarks>
    /// Machine-wide candidates, in the order a platform check picks one:
    /// <list type="bullet">
    /// <item><description>Windows: <c>%ProgramData%\TotallyHotArcRouter</c>.</description></item>
    /// <item><description>
    /// Linux (and other non-Windows, non-macOS Unix): the systemd-provided <c>STATE_DIRECTORY</c>
    /// environment variable when set - the eventual packaged service (migration plan Phase P10) declares
    /// <c>StateDirectory=totallyhot-arcrouter</c> in its unit file, and systemd creates, owns, and passes
    /// the resulting absolute path (not a root to nest a further subdirectory under) before the process
    /// starts. <c>STATE_DIRECTORY</c> can be colon-separated when a unit names more than one directory;
    /// only the first is used here, matching this application's single-directory needs. Falls back to
    /// <c>/var/lib/totallyhot-arcrouter</c> when unset (e.g. a manual, non-systemd run).
    /// </description></item>
    /// <item><description>macOS: <c>/Library/Application Support/TotallyHotArcRouter</c>.</description></item>
    /// </list>
    /// Per-user fallback, tried only when the machine-wide candidate is not writable:
    /// <list type="bullet">
    /// <item><description>Windows: <c>%LocalAppData%\TotallyHotArcRouter</c>.</description></item>
    /// <item><description>Linux: <c>~/.local/share/TotallyHotArcRouter</c> (XDG state-directory convention).</description></item>
    /// <item><description>macOS: <c>~/Library/Application Support/TotallyHotArcRouter</c>.</description></item>
    /// </list>
    /// If even the per-user directory can't be created (a minimal CI sandbox with no writable <c>HOME</c>),
    /// this falls back once more to a <c>TotallyHotArcRouter</c> subdirectory of
    /// <see cref="AppContext.BaseDirectory"/>, mirroring <c>StorageOptions</c>' own last-resort behavior.
    /// </remarks>
    public static string ResolveMachineSharedDirectory()
    {
        return _cachedDirectory ??= ResolveCore();
    }

    private static string ResolveCore()
    {
        var machineWide = MachineWideCandidate();
        if (TryEnsureDirectory(machineWide)) return machineWide;

        var perUser = PerUserCandidate();
        if (TryEnsureDirectory(perUser)) return perUser;

        // Nested under a "data" segment, not AppContext.BaseDirectory + ApplicationDirectoryName directly:
        // a published Linux/macOS executable's own apphost binary sits right in BaseDirectory, named
        // exactly ApplicationDirectoryName (no extension) for this project - Directory.CreateDirectory
        // would collide with that file. Found by real testing (a Linux container) rather than assumed.
        var lastResort = Path.Combine(AppContext.BaseDirectory, "data", ApplicationDirectoryName);
        Directory.CreateDirectory(lastResort);
        return lastResort;
    }

    /// <summary>
    /// Resolves the directory Serilog's file sink writes into (web GUI migration plan Phase P10),
    /// replacing the router's old hardcoded <c>C:\Logs\ArcRouter</c> default. Prefers systemd's own
    /// <c>LOGS_DIRECTORY</c> environment variable when set - the packaged unit declares
    /// <c>LogsDirectory=totallyhot-arcrouter</c>, and systemd creates, owns, and passes the resulting
    /// absolute path before the process starts, mirroring <see cref="ResolveMachineSharedDirectory"/>'s
    /// own <c>STATE_DIRECTORY</c> handling (including the same colon-separated multi-directory rule).
    /// Falls back to a <c>logs</c> subdirectory of <see cref="ResolveMachineSharedDirectory"/> everywhere
    /// else - Windows, macOS, and a non-systemd Linux run. Not memoized and performs no directory
    /// creation of its own: Serilog's file sink creates its target directory on first write.
    /// </summary>
    public static string ResolveLogsDirectory()
    {
        var logsDirectory = Environment.GetEnvironmentVariable("LOGS_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(logsDirectory))
            return logsDirectory.Split(separator: ':', count: 2)[0];

        return Path.Combine(ResolveMachineSharedDirectory(), "logs");
    }

    private static string MachineWideCandidate()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ApplicationDirectoryName);

        if (OperatingSystem.IsMacOS())
            return Path.Combine("/Library/Application Support", ApplicationDirectoryName);

        var stateDirectory = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(stateDirectory))
            return stateDirectory.Split(separator: ':', count: 2)[0];

        return "/var/lib/totallyhot-arcrouter";
    }

    private static string PerUserCandidate()
    {
        // .NET's SpecialFolder enum uses the XDG convention on every Unix, macOS included - the same
        // ~/.local/share it uses on Linux - rather than macOS's own ~/Library/Application Support, so
        // macOS needs an explicit path to get the native-feeling location the plan calls for.
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home)
                ? Path.Combine(AppContext.BaseDirectory, ApplicationDirectoryName)
                : Path.Combine(home, "Library", "Application Support", ApplicationDirectoryName);
        }

        // Windows: %LocalAppData%. Linux: LocalApplicationData already resolves to the XDG
        // $XDG_DATA_HOME default (~/.local/share) per .NET's own convention on Unix.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, ApplicationDirectoryName)
            : Path.Combine(localAppData, ApplicationDirectoryName);
    }

    /// <summary>
    /// Attempts to create <paramref name="directory"/> (idempotent when it already exists), returning
    /// whether it is usable - <see langword="false"/> on any permission or I/O failure, never throwing, so
    /// the caller can move on to its next candidate.
    /// </summary>
    private static bool TryEnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
