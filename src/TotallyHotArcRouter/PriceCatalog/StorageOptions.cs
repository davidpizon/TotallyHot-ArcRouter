namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Shared top-level storage configuration bound from the <c>Storage</c> section. Holds the single
/// on-disk location of the SQLite database that the price catalog and the (future) usage ledger both
/// open.
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
    /// Gets the number of days a <c>usage_ledger</c> row is retained before the startup health check's
    /// retention sweep deletes it, keyed on the row's <c>occurred_at_utc</c>. Defaults to 370 - a year plus
    /// slack, matching the token-monitor plan's bounded-archive discipline (long enough for annual
    /// spend/usage comparisons, bounded so the table doesn't grow forever on a long-lived install).
    /// </summary>
    public int UsageLedgerRetentionDays { get; init; } = 370;

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
    public string ResolveDatabasePath()
    {
        var expanded = Environment.ExpandEnvironmentVariables(DatabasePath);

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

