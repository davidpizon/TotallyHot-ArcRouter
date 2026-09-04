using System.Globalization;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Persists <see cref="ProviderCostReconciliationEntry"/> snapshots and the per-provider reconciliation
/// checkpoint cursor (§5.8), sharing <c>agent_telemetry.db</c> with the rest of the price catalog -
/// mirrors <see cref="UsageLedger"/>'s direct-<see cref="PriceCatalogDatabase"/> style.
/// </summary>
public interface IProviderCostReconciliationStore
{
    /// <summary>Inserts one reconciliation snapshot. Never overwritten - a history of drift over time is preserved.</summary>
    void InsertReconciliation(ProviderCostReconciliationEntry entry);

    /// <summary>
    /// Gets the last fully-reconciled day for <paramref name="provider"/>, or <see langword="null"/> if none has run
    /// yet.
    /// </summary>
    DateOnly? GetLastReconciledDay(string provider);

    /// <summary>Advances the checkpoint cursor for <paramref name="provider"/> to <paramref name="day"/>.</summary>
    void SetLastReconciledDay(string provider, DateOnly day);
}

/// <inheritdoc cref="IProviderCostReconciliationStore"/>
public sealed class ProviderCostReconciliationStore : IProviderCostReconciliationStore
{
    // Round-trip UTC ISO 8601, matching every other timestamp column in this database.
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";
    private const string DayFormat = "yyyy-MM-dd";

    private readonly PriceCatalogDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="ProviderCostReconciliationStore"/> class.</summary>
    public ProviderCostReconciliationStore(PriceCatalogDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc/>
    public void InsertReconciliation(ProviderCostReconciliationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO provider_cost_reconciliation (
                                  provider, window_start_utc, window_end_utc, provider_reported_cost_usd,
                                  local_estimated_cost_usd, scope_note, fetched_at_utc)
                              VALUES ($provider, $windowStart, $windowEnd, $reportedCost, $localCost, $scopeNote, $fetchedAt);
                              """;
        command.Parameters.AddWithValue(parameterName: "$provider", value: entry.Provider);
        command.Parameters.AddWithValue(parameterName: "$windowStart",
            value: entry.WindowStartUtc.UtcDateTime.ToString(format: TimestampFormat,
                provider: CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$windowEnd",
            value: entry.WindowEndUtc.UtcDateTime.ToString(format: TimestampFormat,
                provider: CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$reportedCost",
            value: entry.ProviderReportedCostUsd.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$localCost",
            value: entry.LocalEstimatedCostUsd.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$scopeNote", value: entry.ScopeNote);
        command.Parameters.AddWithValue(parameterName: "$fetchedAt",
            value: entry.FetchedAtUtc.UtcDateTime.ToString(format: TimestampFormat,
                provider: CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public DateOnly? GetLastReconciledDay(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_reconciled_day FROM reconciliation_checkpoint WHERE provider = $provider;";
        command.Parameters.AddWithValue(parameterName: "$provider", value: provider);

        return command.ExecuteScalar() is string stored
            ? DateOnly.ParseExact(s: stored, format: DayFormat, provider: CultureInfo.InvariantCulture)
            : null;
    }

    /// <inheritdoc/>
    public void SetLastReconciledDay(string provider, DateOnly day)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO reconciliation_checkpoint (provider, last_reconciled_day)
                              VALUES ($provider, $day)
                              ON CONFLICT(provider) DO UPDATE SET last_reconciled_day = excluded.last_reconciled_day;
                              """;
        command.Parameters.AddWithValue(parameterName: "$provider", value: provider);
        command.Parameters.AddWithValue(parameterName: "$day",
            value: day.ToString(format: DayFormat, provider: CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}