namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Shared top-level storage configuration bound from the <c>Storage</c> section. Holds the on-disk
/// locations of two separate SQLite databases: the operational database (<see cref="DatabasePath"/>)
/// that the price catalog and the (future) usage ledger both open, and the CodeRouterBench corpus
/// database (<see cref="BenchmarkDatabasePath"/>), a separate database written only during explicit
/// sync operations.
/// </summary>
/// <remarks>
/// The database path lives here rather than under <c>PriceCatalog</c> or <c>CostTracking</c> on purpose:
/// both features share one file (<c>agent_telemetry.db</c>), and two settings for one file could
/// disagree - see <c>docs/router/agent-cost-tracking.md</c> §4 and
/// <c>docs/router/model-price-catalog.md</c>'s Configuration section. Either feature owning it would make
/// the other's storage hostage to a section it has no other reason to configure.
/// <para>
/// Every default below is machine-wide (<c>%ProgramData%\TotallyHotArcRouter\</c>), not the per-user
/// <c>%LOCALAPPDATA%\</c> they used to be, for the same reason as
/// <see cref="TotallyHot.ArcRouter.Router.RoutingGateStore"/> and
/// <see cref="TotallyHot.ArcRouter.Proxy.Management.ManagementAccessToken"/>: the installed service runs
/// as <c>LocalSystem</c> (<c>TotallyHotArcRouter.Installer/Package.wxs</c>), whose <c>%LOCALAPPDATA%</c>
/// is <c>C:\Windows\System32\config\systemprofile\AppData\Local\</c> - an administrators-only directory.
/// The operational data was therefore unreadable by the interactive user for backup or inspection, and
/// any database left behind by running the router directly was silently orphaned from the service's.
/// Unlike the token, nothing outside the router process opens these files (the GUI reads usage through
/// the proxy's <c>/admin/usage/*</c> endpoints - see <c>docs/router/telemetry.md</c>), so this move is an
/// operability fix rather than a functional one.
/// </para>
/// <para>
/// The move also collapses a folder-name split: <c>appsettings.json</c> pinned <c>DatabasePath</c> under
/// <c>TotallyHotArcRouter\</c> while the four other defaults used <c>TotallyHot.ArcRouter\</c>, so one
/// install wrote two sibling directories. All five now share
/// <see cref="MachineSharedDirectoryName"/>. <see cref="LegacyStorageMigration"/> adopts files from
/// either old spelling on first run.
/// </para>
/// <para>
/// These files inherit <c>%ProgramData%</c>'s default ACL, which grants <c>Users</c> read - enough to
/// copy a database for inspection, but not to write one. A developer running the router directly against
/// a tree the service created as <c>LocalSystem</c> will therefore see SQLite report a read-only
/// database; delete the file (or run elevated) to have it recreated under the developer's own account.
/// Note this also means <c>transcripts.db</c>, when <c>TranscriptOptions.Enabled</c> turns it on, exposes
/// raw prompt and response text to every local account - see <c>docs/router/secrets-at-rest.md</c>.
/// </para>
/// </remarks>
public sealed class StorageOptions
{
    /// <summary>Gets the configuration section name used for shared storage settings.</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// Gets the database file path. May contain environment-variable tokens (<c>%PROGRAMDATA%</c> and
    /// <c>%LOCALAPPDATA%</c> are both recognized) and Windows-style separators, all normalized by
    /// <see cref="ResolveDatabasePath"/>. A relative path is resolved against the application base
    /// directory, matching <c>SpendTracker</c>'s handling of its log path.
    /// </summary>
    public string DatabasePath { get; init; } = @"%PROGRAMDATA%\TotallyHotArcRouter\agent_telemetry.db";

    /// <summary>
    /// Gets the CodeRouterBench corpus database's file path (docs/router/coderouterbench-sqlite-migration-plan.md).
    /// May contain the same tokens and separators as <see cref="DatabasePath"/>, resolved by
    /// <see cref="ResolveBenchmarkDatabasePath"/>. A separate file from <see cref="DatabasePath"/>: the
    /// corpus is read-only, bulk, and freely re-downloadable, so it does not share a WAL writer lock or a
    /// backup with the operational database.
    /// </summary>
    public string BenchmarkDatabasePath { get; init; } = @"%PROGRAMDATA%\TotallyHotArcRouter\coderouterbench.db";

    /// <summary>
    /// Gets the trained <c>logreg</c> voter model artifact's file path
    /// (docs/router/live-feedback-learning-plan.md Phase 3). May contain the same tokens and separators
    /// as <see cref="DatabasePath"/>, resolved by <see cref="ResolveLogRegModelPath"/>. Per-installation
    /// and never checked in: it is derived from the operator's own synced corpus and live traffic, unlike
    /// the deleted placeholder this replaces (Phase 6).
    /// </summary>
    public string LogRegModelPath { get; init; } = @"%PROGRAMDATA%\TotallyHotArcRouter\logreg_voter_model.json";

    /// <summary>
    /// Gets the opt-in transcript store's file path
    /// (docs/router/self-organizing-classification-plan.md Phase T1a). May contain the same tokens and
    /// separators as <see cref="DatabasePath"/>, resolved by <see cref="ResolveTranscriptDatabasePath"/>.
    /// A separate file from <see cref="DatabasePath"/> and from <c>RouterMemoryDatabase</c>'s file: it
    /// carries raw prompt/response text, which the two existing databases deliberately do not, so its
    /// lifecycle (creation gated on <c>TranscriptOptions.Enabled</c>, retention-bounded) stays independent
    /// of both.
    /// </summary>
    public string TranscriptDatabasePath { get; init; } = @"%PROGRAMDATA%\TotallyHotArcRouter\transcripts.db";

    /// <summary>
    /// Gets the trained self-organizing cluster model artifact's file path
    /// (docs/router/self-organizing-classification-plan.md Phase T2e). May contain the same tokens and
    /// separators as <see cref="DatabasePath"/>, resolved by <see cref="ResolveClusterModelPath"/>.
    /// Per-installation and never checked in, mirroring <see cref="LogRegModelPath"/>'s lifecycle.
    /// </summary>
    public string ClusterModelPath { get; init; } = @"%PROGRAMDATA%\TotallyHotArcRouter\cluster_model.json";

    /// <summary>
    /// Gets the number of days a <c>usage_ledger</c> row is retained before the startup health check's
    /// retention sweep deletes it, keyed on the row's <c>occurred_at_utc</c>. Defaults to 370 - a year plus
    /// slack, matching the token-monitor plan's bounded-archive discipline (long enough for annual
    /// spend/usage comparisons, bounded so the table doesn't grow forever on a long-lived install).
    /// </summary>
    public int UsageLedgerRetentionDays { get; init; } = 370;

    /// <summary>
    /// Gets the timezone id <c>usage_rollup</c> bucket boundaries are computed in, resolved via
    /// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> - an IANA id (<c>"America/Los_Angeles"</c>) on
    /// .NET's ICU-backed globalization (the default on every platform this project targets, including
    /// Windows). An id that can't be resolved on the host falls back to UTC with a logged warning rather
    /// than failing startup - see <c>UsageRollupStore.ResolveTimeZone</c>. Read once, on the first run that
    /// ever creates a rollup bucket, and pinned into <c>rollup_metadata</c> from then on (tokscale's
    /// reproducible-bucket rule - see <c>IUsageRollupStore</c>): changing this afterward has no effect until
    /// the database is recreated, since re-cutting historical buckets would make two reports generated a
    /// month apart over the same past day disagree. Defaults to UTC, since the proxy is typically a
    /// headless service with no single "operator's local day" to prefer.
    /// </summary>
    public string RollupTimezone { get; init; } = "UTC";

    // The token every default above leads with. On non-Windows hosts (the project's Docker default is
    // Linux) PROGRAMDATA is unset, so Environment.ExpandEnvironmentVariables leaves it literal.
    private const string ProgramDataToken = "%PROGRAMDATA%";

    // Still recognized even though no default uses it any more: an operator's existing appsettings.json
    // may pin a %LOCALAPPDATA% path, and LegacyStorageMigration builds the pre-move locations from it.
    private const string LocalAppDataToken = "%LOCALAPPDATA%";

    /// <summary>
    /// The single machine-wide directory every file above lives in, shared with
    /// <c>RoutingGateStore</c>'s state file and <c>ManagementAccessToken</c>'s token. Public so
    /// <see cref="LegacyStorageMigration"/> can tell a default-located file (which it may migrate) from
    /// one an operator deliberately pointed somewhere else (which it must leave alone).
    /// </summary>
    public const string MachineSharedDirectoryName = "TotallyHotArcRouter";

    // The two per-user directories these files lived in before the move to %ProgramData%. Both spellings
    // existed at once: appsettings.json pinned DatabasePath under the dotless name while the four
    // compiled defaults used the dotted one, so a real install can have files in either.
    private static readonly string[] LegacyDirectoryNames = ["TotallyHot.ArcRouter", "TotallyHotArcRouter"];

    /// <summary>
    /// Expands environment-variable tokens in <see cref="DatabasePath"/>, normalizes separators, and
    /// returns an absolute path. A relative value is combined with <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Cross-platform hardening: on Linux, <c>%PROGRAMDATA%</c> and <c>%LOCALAPPDATA%</c> are both
    /// undefined (so they would survive expansion literally) and backslashes are ordinary filename
    /// characters (so directory creation would be skipped and the file created with an odd name). This
    /// substitutes a real folder for either unexpanded token - see <see cref="MachineSharedRoot"/> for why
    /// the <c>%PROGRAMDATA%</c> fallback is not <c>CommonApplicationData</c> off Windows - and rewrites
    /// backslashes to the platform separator, so the same default works on Windows and in the Linux
    /// container.
    /// </remarks>
    public string ResolveDatabasePath() => ResolvePath(DatabasePath);

    /// <summary>
    /// Expands environment-variable tokens in <see cref="BenchmarkDatabasePath"/>, normalizes separators,
    /// and returns an absolute path, via the same rules as <see cref="ResolveDatabasePath"/>.
    /// </summary>
    public string ResolveBenchmarkDatabasePath() => ResolvePath(BenchmarkDatabasePath);

    /// <summary>
    /// Expands environment-variable tokens in <see cref="LogRegModelPath"/>, normalizes separators, and
    /// returns an absolute path, via the same rules as <see cref="ResolveDatabasePath"/>.
    /// </summary>
    public string ResolveLogRegModelPath() => ResolvePath(LogRegModelPath);

    /// <summary>
    /// Expands environment-variable tokens in <see cref="TranscriptDatabasePath"/>, normalizes
    /// separators, and returns an absolute path, via the same rules as <see cref="ResolveDatabasePath"/>.
    /// </summary>
    public string ResolveTranscriptDatabasePath() => ResolvePath(TranscriptDatabasePath);

    /// <summary>
    /// Expands environment-variable tokens in <see cref="ClusterModelPath"/>, normalizes separators, and
    /// returns an absolute path, via the same rules as <see cref="ResolveDatabasePath"/>.
    /// </summary>
    public string ResolveClusterModelPath() => ResolvePath(ClusterModelPath);

    /// <summary>
    /// Shared expansion/normalization logic behind <see cref="ResolveDatabasePath"/> and
    /// <see cref="ResolveBenchmarkDatabasePath"/> - see their remarks for why each substitution exists.
    /// </summary>
    private static string ResolvePath(string rawPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(rawPath);

        // Fall back to the platform's shared-application-data folder if the token survived (PROGRAMDATA
        // unset), rather than creating a file literally named "%PROGRAMDATA%".
        if (expanded.Contains(ProgramDataToken, StringComparison.OrdinalIgnoreCase))
        {
            expanded = expanded.Replace(ProgramDataToken, MachineSharedRoot(), StringComparison.OrdinalIgnoreCase);
        }

        // Same substitution for the pre-move token, which no default uses any more but a pinned
        // appsettings.json value (and every path LegacyStorageMigration probes) still can.
        if (expanded.Contains(LocalAppDataToken, StringComparison.OrdinalIgnoreCase))
        {
            expanded = expanded.Replace(LocalAppDataToken, PerUserRoot(), StringComparison.OrdinalIgnoreCase);
        }

        // Treat Windows-style backslashes as separators everywhere, so a backslash path resolves (and its
        // directory is created) on Linux too.
        expanded = expanded.Replace('\\', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(AppContext.BaseDirectory, expanded);
    }

    /// <summary>
    /// Resolves the machine-wide application-data root a <c>%PROGRAMDATA%</c> token stands for, trimmed of
    /// any trailing separator so the substitution never produces a doubled one.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> <see cref="Environment.SpecialFolder.CommonApplicationData"/> off Windows:
    /// there it resolves to <c>/usr/share</c>, which the unprivileged account in this project's Linux
    /// container cannot write. The container has no LocalSystem/interactive-user split to bridge in the
    /// first place - that split is the only reason these files are machine-wide - so the per-user root is
    /// both writable and correct there.
    /// </remarks>
    private static string MachineSharedRoot()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Some minimal Linux environments (e.g. a CI runner with no XDG/HOME) return an empty folder here;
        // fall back to the writable application base directory rather than rooting at "/".
        if (string.IsNullOrEmpty(root))
        {
            root = AppContext.BaseDirectory;
        }

        return root.TrimEnd('/', '\\');
    }

    /// <summary>
    /// Resolves the per-user application-data root a <c>%LOCALAPPDATA%</c> token stands for, with the same
    /// empty-folder fallback and trailing-separator trim as <see cref="MachineSharedRoot"/>.
    /// </summary>
    private static string PerUserRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrEmpty(root))
        {
            root = AppContext.BaseDirectory;
        }

        return root.TrimEnd('/', '\\');
    }

    /// <summary>
    /// Gets the resolved machine-wide directory the defaults above live in
    /// (<c>%ProgramData%\TotallyHotArcRouter</c> on Windows).
    /// </summary>
    public static string ResolveMachineSharedDirectory() =>
        Path.Combine(MachineSharedRoot(), MachineSharedDirectoryName);

    /// <summary>
    /// Gets the resolved per-user directories these files lived in before the move to
    /// <c>%ProgramData%</c>, newest spelling first. <see cref="LegacyStorageMigration"/> probes each in
    /// order for a file to adopt.
    /// </summary>
    public static IReadOnlyList<string> ResolveLegacyDirectories() =>
        [.. LegacyDirectoryNames.Select(name => Path.Combine(PerUserRoot(), name))];
}

