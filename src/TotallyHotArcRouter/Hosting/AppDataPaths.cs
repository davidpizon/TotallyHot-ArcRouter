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

    // Set only by RedirectForTesting; null in every real process.
    private static string? _perUserRootOverride;

    /// <summary>
    /// Pins <see cref="ResolveMachineSharedDirectory"/> to <paramref name="machineSharedDirectory"/> and
    /// <see cref="ResolvePerUserRoot"/> to <paramref name="perUserRoot"/> for the rest of this process.
    /// Test-only: the router's test assembly calls it from a module initializer, before any test code
    /// runs, so no test can read or write the real machine's data directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every default path in the router is built on these two resolvers - the CA and leaf certificates,
    /// <c>secrets.dat</c>, the routing-gate and discovery files, the operator's
    /// <c>appsettings.local.json</c> overlay, the log directory, the model caches, and every
    /// <c>%PROGRAMDATA%</c> storage default - so redirecting both here isolates all of them at once. An
    /// environment variable cannot do the same on Windows: the machine-wide candidate comes from
    /// <see cref="Environment.SpecialFolder.CommonApplicationData"/>. Without this, a plain
    /// <c>dotnet test</c> run rewrote the real <c>web-interface.json</c> and churned the real
    /// <c>logs</c> and <c>models</c> directories (2026-10-02).
    /// </para>
    /// <para>
    /// The per-user root is redirected too because <see cref="PriceCatalog.LegacyStorageMigration"/>
    /// adopts files from it into the machine-shared directory and renames the originals aside. With only
    /// the machine-shared directory redirected, a test running that migration with default options would
    /// adopt a developer's real pre-move files into a temp directory and rename the real ones.
    /// </para>
    /// <para>
    /// Throws once the directory has been resolved by any means. A redirect that arrives after the first
    /// resolution is too late to protect anything, so it must fail loudly rather than appear to work.
    /// </para>
    /// </remarks>
    /// <param name="machineSharedDirectory">The directory to use in place of the machine-shared one; created if missing.</param>
    /// <param name="perUserRoot">The directory to use in place of the per-user application-data root.</param>
    /// <exception cref="InvalidOperationException">The machine-shared directory has already been resolved.</exception>
    internal static void RedirectForTesting(string machineSharedDirectory, string perUserRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineSharedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(perUserRoot);

        if (_cachedDirectory is not null)
            throw new InvalidOperationException(
                $"The machine-shared data directory was already resolved to '{_cachedDirectory}'; " +
                "RedirectForTesting must run before anything resolves it.");

        Directory.CreateDirectory(machineSharedDirectory);
        _perUserRootOverride = perUserRoot;
        _cachedDirectory = machineSharedDirectory;
    }

    /// <summary>
    /// Resolves the machine-shared data directory for this platform, preferring a true machine-wide
    /// location and falling back to a per-user one only when the machine-wide candidate can't be created
    /// or written to - the common case for an unprivileged developer running the router directly, with no
    /// dedicated service account and no elevation. The result is memoized for the process's lifetime;
    /// under test it is the directory <see cref="RedirectForTesting"/> pinned instead.
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

    /// <summary>Resolves this platform's candidates and picks the first usable one, uncached.</summary>
    private static string ResolveCore()
    {
        // Nested under a "data" segment, not AppContext.BaseDirectory + ApplicationDirectoryName directly:
        // a published Linux/macOS executable's own apphost binary sits right in BaseDirectory, named
        // exactly ApplicationDirectoryName (no extension) for this project - Directory.CreateDirectory
        // would collide with that file. Found by real testing (a Linux container) rather than assumed.
        return SelectUsableDirectory(machineWide: MachineWideCandidate(), perUser: PerUserCandidate(),
            lastResort: Path.Combine(AppContext.BaseDirectory, "data", ApplicationDirectoryName));
    }

    /// <summary>
    /// Returns <paramref name="machineWide"/> if this process can create and write into it, otherwise
    /// <paramref name="perUser"/> under the same test, otherwise <paramref name="lastResort"/> (created
    /// unconditionally) - the fallback chain <see cref="ResolveMachineSharedDirectory"/>'s remarks
    /// describe. Kept separate from candidate selection so tests can drive the chain against temp
    /// directories instead of write-probing the real machine-wide one.
    /// </summary>
    /// <param name="machineWide">The preferred, machine-wide directory.</param>
    /// <param name="perUser">The per-user fallback.</param>
    /// <param name="lastResort">The directory used when neither candidate is writable.</param>
    internal static string SelectUsableDirectory(string machineWide, string perUser, string lastResort)
    {
        if (TryEnsureDirectory(machineWide)) return machineWide;
        if (TryEnsureDirectory(perUser)) return perUser;

        Directory.CreateDirectory(lastResort);
        return lastResort;
    }

    /// <summary>
    /// Resolves the per-user application-data root: <see cref="Environment.SpecialFolder.LocalApplicationData"/>,
    /// or <see cref="AppContext.BaseDirectory"/> when the platform reports none, with any trailing
    /// separator trimmed. It is the root <see cref="PriceCatalog.StorageOptions"/>' defaults lived under
    /// before they moved to the machine-shared directory, so a <c>%LOCALAPPDATA%</c> storage token expands
    /// to it and <see cref="PriceCatalog.LegacyStorageMigration"/> looks under it for files to adopt.
    /// Honors <see cref="RedirectForTesting"/>.
    /// </summary>
    internal static string ResolvePerUserRoot()
    {
        if (_perUserRootOverride is not null) return _perUserRootOverride;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrEmpty(root)) root = AppContext.BaseDirectory;

        return root.TrimEnd('/', '\\');
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

    /// <summary>
    /// Gets this platform's machine-wide candidate directory, as listed in
    /// <see cref="ResolveMachineSharedDirectory"/>'s remarks. Pure: performs no I/O and ignores
    /// <see cref="RedirectForTesting"/>, so it always names the real location.
    /// </summary>
    internal static string MachineWideCandidate()
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

    /// <summary>
    /// Gets this platform's per-user fallback directory, as listed in
    /// <see cref="ResolveMachineSharedDirectory"/>'s remarks. Pure: performs no I/O and ignores
    /// <see cref="RedirectForTesting"/>, so it always names the real location.
    /// </summary>
    internal static string PerUserCandidate()
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
    /// Attempts to create <paramref name="directory"/> (idempotent when it already exists) and confirms
    /// this process can actually write into it, returning whether it is usable -
    /// <see langword="false"/> on any permission or I/O failure, never throwing, so the caller can move
    /// on to its next candidate.
    /// </summary>
    /// <remarks>
    /// <see cref="Directory.CreateDirectory(string)"/> alone is not enough: it is a silent no-op when
    /// the directory already exists, regardless of whether *this* account can write to it - a real gap
    /// on a machine where the machine-wide candidate was created by a previous, more-privileged install
    /// (root, a different service account) and is now read-only to whoever is running the router today.
    /// Without an actual write probe, that case was reported "usable" and every later write (secrets.dat,
    /// the operational databases) failed instead of falling through to the per-user fallback this method
    /// exists to enable. The probe creates and deletes a uniquely-named temp file inside the directory,
    /// so it exercises the exact permission a real write needs rather than just directory metadata.
    /// </remarks>
    private static bool TryEnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var probePath = Path.Combine(directory, $".totallyhotarcrouter-write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(path: probePath, bytes: []);
            File.Delete(probePath);

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
