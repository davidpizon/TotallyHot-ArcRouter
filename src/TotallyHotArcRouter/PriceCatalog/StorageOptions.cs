namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Shared top-level storage configuration bound from the <c>Storage</c> section. Holds the on-disk
/// locations of two separate SQLite databases: the operational database (<see cref="DatabasePath"/>)
/// that the price catalog and the (future) usage ledger both open, and the read-only CodeRouterBench
/// corpus database (<see cref="BenchmarkDatabasePath"/>).
/// </summary>
/// <remarks>
/// The database path lives here rather than under <c>PriceCatalog</c> or <c>CostTracking</c> on purpose:
/// both features share one file (<c>agent_telemetry.db</c>), and two settings for one file could
/// disagree - see <c>docs/router/agent-cost-tracking.md</c> §4 and
/// <c>docs/router/model-price-catalog.md</c>'s Configuration section. Either feature owning it would make
/// the other's storage hostage to a section it has no other reason to configure.
/// </remarks>
public sealed class StorageOptions
{
    /// <summary>Gets the configuration section name used for shared storage settings.</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// Gets the database file path. May contain environment-variable tokens (e.g.
    /// <c>%LOCALAPPDATA%</c>) and Windows-style separators, both normalized by
    /// <see cref="ResolveDatabasePath"/>. A relative path is resolved against the application base
    /// directory, matching <c>SpendTracker</c>'s handling of its log path.
    /// </summary>
    public string DatabasePath { get; init; } = @"%LOCALAPPDATA%\TotallyHot.ArcRouter\agent_telemetry.db";

    /// <summary>
    /// Gets the CodeRouterBench corpus database's file path (docs/router/coderouterbench-sqlite-migration-plan.md).
    /// May contain the same tokens and separators as <see cref="DatabasePath"/>, resolved by
    /// <see cref="ResolveBenchmarkDatabasePath"/>. A separate file from <see cref="DatabasePath"/>: the
    /// corpus is read-only, bulk, and freely re-downloadable, so it does not share a WAL writer lock or a
    /// backup with the operational database.
    /// </summary>
    public string BenchmarkDatabasePath { get; init; } = @"%LOCALAPPDATA%\TotallyHot.ArcRouter\coderouterbench.db";

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

    // The default's leading token. On non-Windows hosts (the project's Docker default is Linux)
    // LOCALAPPDATA is typically unset, so Environment.ExpandEnvironmentVariables leaves it literal.
    private const string LocalAppDataToken = "%LOCALAPPDATA%";

    /// <summary>
    /// Expands environment-variable tokens in <see cref="DatabasePath"/>, normalizes separators, and
    /// returns an absolute path. A relative value is combined with <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Cross-platform hardening: on Linux, <c>%LOCALAPPDATA%</c> is usually undefined (so it would survive
    /// expansion literally) and backslashes are ordinary filename characters (so directory creation would
    /// be skipped and the file created with an odd name). This substitutes the cross-platform
    /// local-application-data folder for an unexpanded <c>%LOCALAPPDATA%</c> and rewrites backslashes to
    /// the platform separator, so the same default works on Windows and in the Linux container.
    /// </remarks>
    public string ResolveDatabasePath() => ResolvePath(DatabasePath);

    /// <summary>
    /// Expands environment-variable tokens in <see cref="BenchmarkDatabasePath"/>, normalizes separators,
    /// and returns an absolute path, via the same rules as <see cref="ResolveDatabasePath"/>.
    /// </summary>
    public string ResolveBenchmarkDatabasePath() => ResolvePath(BenchmarkDatabasePath);

    /// <summary>
    /// Shared expansion/normalization logic behind <see cref="ResolveDatabasePath"/> and
    /// <see cref="ResolveBenchmarkDatabasePath"/> - see their remarks for why each substitution exists.
    /// </summary>
    private static string ResolvePath(string rawPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(rawPath);

        // Fall back to the platform's local-app-data folder if the token survived (LOCALAPPDATA unset),
        // rather than creating a file literally named "%LOCALAPPDATA%".
        if (expanded.Contains(LocalAppDataToken, StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Some minimal Linux environments (e.g. a CI runner with no XDG/HOME) return an empty folder
            // here; fall back to the writable application base directory rather than rooting at "/".
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = AppContext.BaseDirectory;
            }

            // Trim a trailing separator (BaseDirectory carries one) so the substitution never produces a
            // doubled separator.
            localAppData = localAppData.TrimEnd('/', '\\');
            expanded = expanded.Replace(LocalAppDataToken, localAppData, StringComparison.OrdinalIgnoreCase);
        }

        // Treat Windows-style backslashes as separators everywhere, so a backslash path resolves (and its
        // directory is created) on Linux too.
        expanded = expanded.Replace('\\', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(AppContext.BaseDirectory, expanded);
    }
}

