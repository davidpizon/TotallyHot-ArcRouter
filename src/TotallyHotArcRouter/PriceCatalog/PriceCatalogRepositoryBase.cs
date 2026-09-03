using Microsoft.Data.Sqlite;
using System.Globalization;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Shared connection-bootstrap and timestamp plumbing for every price-catalog repository
/// (<see cref="PriceRepository"/>, <see cref="PriceSourceRepository"/>, <see cref="ProviderBudgetRepository"/>,
/// <see cref="ProviderSpendRepository"/>, <see cref="RateLimitRepository"/>, and
/// <see cref="ReportedUsageRepository"/>). Each of those types owns one confirmed concern split out of the
/// former monolithic <c>PriceCatalogRepository</c>; this base is the one piece all six need without
/// diverging - opening a connection against the shared <see cref="PriceCatalogDatabase"/>, and formatting/
/// parsing the round-trip UTC timestamp every table stores prices, spend, and captures under.
/// </summary>
public abstract class PriceCatalogRepositoryBase
{
    // Round-trip UTC ISO 8601 (e.g. 2026-07-16T12:34:56.7890000Z). Fixed length and lexicographically
    // ordered, so freshness comparisons need no date parsing in SQL.
    private protected const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private readonly PriceCatalogDatabase _database;

    /// <summary>Initializes the shared database dependency for a derived repository.</summary>
    /// <param name="database">The catalog database this repository reads from and writes to.</param>
    private protected PriceCatalogRepositoryBase(PriceCatalogDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Opens a new connection against the shared price-catalog database.</summary>
    private protected SqliteConnection OpenConnection() => _database.OpenConnection();

    /// <summary>
    /// Formats the UTC timestamp that is <paramref name="maxAge"/> before now, for use as a freshness cutoff
    /// in a SQL comparison.
    /// </summary>
    private protected static string FormatCutoff(TimeSpan maxAge) =>
        (DateTimeOffset.UtcNow - maxAge).UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a round-trip UTC timestamp written in <see cref="TimestampFormat"/> back into a
    /// <see cref="DateTimeOffset"/>.
    /// </summary>
    private protected static DateTimeOffset ParseTimestamp(string value) =>
        new(
            DateTime.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            TimeSpan.Zero);
}
