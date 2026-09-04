using System.Globalization;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// One (provider, day, model) row of a provider's own reported token usage
/// (docs/router/secrets-at-rest-plan.md §8.1), as stored in <c>provider_reported_usage_snapshot</c>. Raw
/// counts as reported - no derived totals.
/// </summary>
/// <param name="UsageDay">The UTC calendar day this usage was reported for.</param>
/// <param name="Model">The provider's own model identifier for this row.</param>
/// <param name="InputTokens">Uncached input tokens for this day/model.</param>
/// <param name="OutputTokens">Output tokens for this day/model.</param>
/// <param name="CacheCreationTokens">Cache-creation (cache-write) input tokens for this day/model.</param>
/// <param name="CacheReadTokens">Cache-read input tokens for this day/model.</param>
public sealed record ReportedUsageRow(
    DateOnly UsageDay,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens);

/// <summary>
/// Thin ADO.NET wrapper over the reported-usage persistence table (<c>provider_reported_usage_snapshot</c>).
/// Split out of the former monolithic <c>PriceCatalogRepository</c> (docs/router/code-smell-refactoring-plan.md
/// M3) - the other five concerns that repository once mixed together each now have their own type in this
/// namespace.
/// </summary>
public sealed class ReportedUsageRepository : PriceCatalogRepositoryBase
{
    // Reported usage older than this is dropped on every write, mirroring the rate-limit history's
    // retention pattern: AnthropicUsageReportService always re-fetches a 30-day trailing window, so nothing
    // legitimate is ever this old, and a day that's aged out only lingers because the provider stopped
    // reporting it.
    private static readonly TimeSpan ReportedUsageRetention = TimeSpan.FromDays(45);

    /// <summary>Initializes a new instance of the <see cref="ReportedUsageRepository"/> class.</summary>
    /// <param name="database">The catalog database.</param>
    public ReportedUsageRepository(PriceCatalogDatabase database)
        : base(database)
    {
    }

    /// <summary>
    /// Upserts one provider's reported usage rows (docs/router/secrets-at-rest-plan.md §8.1), replacing any
    /// existing row for the same (provider, day, model), then prunes rows older than
    /// <see cref="ReportedUsageRetention"/> for this provider. A no-op when <paramref name="rows"/> is
    /// empty - a failed or empty fetch must never wipe out an existing snapshot.
    /// </summary>
    public void UpsertReportedUsage(string providerKey, IReadOnlyList<ReportedUsageRow> rows,
        DateTimeOffset fetchedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0) return;

        var fetchedAt =
            fetchedAtUtc.UtcDateTime.ToString(format: TimestampFormat, provider: CultureInfo.InvariantCulture);
        var cutoff = fetchedAtUtc.UtcDateTime.AddTicks(-ReportedUsageRetention.Ticks)
            .ToString(format: "yyyy-MM-dd", provider: CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                                  INSERT INTO provider_reported_usage_snapshot
                                      (provider_key, usage_day, model, input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens, fetched_at_utc)
                                  VALUES ($key, $day, $model, $input, $output, $cacheCreate, $cacheRead, $fetchedAt)
                                  ON CONFLICT(provider_key, usage_day, model) DO UPDATE SET
                                      input_tokens          = excluded.input_tokens,
                                      output_tokens         = excluded.output_tokens,
                                      cache_creation_tokens = excluded.cache_creation_tokens,
                                      cache_read_tokens     = excluded.cache_read_tokens,
                                      fetched_at_utc        = excluded.fetched_at_utc;
                                  """;
            command.Parameters.AddWithValue(parameterName: "$key", value: providerKey);
            command.Parameters.AddWithValue(parameterName: "$day",
                value: row.UsageDay.ToString(format: "yyyy-MM-dd", provider: CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(parameterName: "$model", value: row.Model);
            command.Parameters.AddWithValue(parameterName: "$input", value: row.InputTokens);
            command.Parameters.AddWithValue(parameterName: "$output", value: row.OutputTokens);
            command.Parameters.AddWithValue(parameterName: "$cacheCreate", value: row.CacheCreationTokens);
            command.Parameters.AddWithValue(parameterName: "$cacheRead", value: row.CacheReadTokens);
            command.Parameters.AddWithValue(parameterName: "$fetchedAt", value: fetchedAt);
            command.ExecuteNonQuery();
        }

        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText =
                "DELETE FROM provider_reported_usage_snapshot WHERE provider_key = $key AND usage_day < $cutoff;";
            prune.Parameters.AddWithValue(parameterName: "$key", value: providerKey);
            prune.Parameters.AddWithValue(parameterName: "$cutoff", value: cutoff);
            prune.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Returns every reported-usage row currently stored for <paramref name="providerKey"/>, ordered by day
    /// then model, along with the most recent <c>fetched_at_utc</c> across those rows. Returns an empty row
    /// list and a <see langword="null"/> instant when nothing has ever been fetched for this provider.
    /// </summary>
    public (IReadOnlyList<ReportedUsageRow> Rows, DateTimeOffset? FetchedAtUtc) GetReportedUsage(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT usage_day, model, input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens, fetched_at_utc
                              FROM provider_reported_usage_snapshot
                              WHERE provider_key = $key
                              ORDER BY usage_day, model;
                              """;
        command.Parameters.AddWithValue(parameterName: "$key", value: providerKey);

        var rows = new List<ReportedUsageRow>();
        DateTimeOffset? latest = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var day = DateOnly.ParseExact(s: reader.GetString(0), format: "yyyy-MM-dd",
                provider: CultureInfo.InvariantCulture);
            rows.Add(new ReportedUsageRow(
                UsageDay: day,
                Model: reader.GetString(1),
                InputTokens: reader.GetInt64(2),
                OutputTokens: reader.GetInt64(3),
                CacheCreationTokens: reader.GetInt64(4),
                CacheReadTokens: reader.GetInt64(5)));

            var fetchedAt = ParseTimestamp(reader.GetString(6));
            if (latest is null || fetchedAt > latest) latest = fetchedAt;
        }

        return (rows, latest);
    }
}