using System.Globalization;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// A provider's spend accumulated within one <c>YYYY-MM</c> period. Tokens are the sum of prompt and
/// completion tokens billed to the provider that actually served each request.
/// </summary>
/// <param name="ProviderKey">The provider key.</param>
/// <param name="CostUsd">Total estimated USD spent in the period.</param>
/// <param name="PromptTokens">Total prompt tokens (raw, post-cache-breakpoint <c>input_tokens</c>) in the period.</param>
/// <param name="CompletionTokens">Total completion tokens in the period.</param>
/// <param name="CacheCreationTokens">Total cache-creation (cache-write) input tokens in the period.</param>
/// <param name="CacheReadTokens">Total cache-read input tokens in the period.</param>
/// <param name="LastUsageAtUtc">
/// The UTC instant the most recent usage in this period was recorded, or <see langword="null"/> if no
/// usage has been recorded for this period yet.
/// </param>
public sealed record ProviderSpendRow(
    string ProviderKey,
    decimal CostUsd,
    long PromptTokens,
    long CompletionTokens,
    long CacheCreationTokens = 0,
    long CacheReadTokens = 0,
    DateTimeOffset? LastUsageAtUtc = null);

/// <summary>
/// Thin ADO.NET wrapper over the provider-spend accounting table (<c>provider_spend</c>). Split out of the
/// former monolithic <c>PriceCatalogRepository</c> (docs/router/code-smell-refactoring-plan.md M3) - the
/// other five concerns that repository once mixed together each now have their own type in this namespace.
/// </summary>
public sealed class ProviderSpendRepository : PriceCatalogRepositoryBase
{
    /// <summary>Initializes a new instance of the <see cref="ProviderSpendRepository"/> class.</summary>
    /// <param name="database">The catalog database.</param>
    public ProviderSpendRepository(PriceCatalogDatabase database)
        : base(database)
    {
    }

    /// <summary>
    /// Returns every provider's accumulated spend for the given <c>YYYY-MM</c> <paramref name="period"/>.
    /// Providers with no usage in the period are absent.
    /// </summary>
    public IReadOnlyList<ProviderSpendRow> GetProviderSpend(string period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(period);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_key, cost_usd, prompt_tokens, completion_tokens,
                   cache_creation_tokens, cache_read_tokens, last_usage_at
            FROM provider_spend
            WHERE period = $period;
            """;
        command.Parameters.AddWithValue("$period", period);

        var rows = new List<ProviderSpendRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ProviderSpendRow(
                ProviderKey: reader.GetString(0),
                CostUsd: decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                PromptTokens: reader.GetInt64(2),
                CompletionTokens: reader.GetInt64(3),
                CacheCreationTokens: reader.GetInt64(4),
                CacheReadTokens: reader.GetInt64(5),
                LastUsageAtUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6))));
        }

        return rows;
    }

    /// <summary>
    /// Adds one request's usage to a provider's spend for the given <c>YYYY-MM</c> <paramref name="period"/>,
    /// creating the period row on first use. The cost is accumulated as a true decimal via read-modify-write
    /// inside a transaction (not SQL float arithmetic) so a month of small per-request costs doesn't drift.
    /// Cache token columns accumulate via SQL <c>+</c>, like <paramref name="promptTokens"/>/
    /// <paramref name="completionTokens"/>. <paramref name="usageAtUtc"/> only ever advances the stored
    /// <c>last_usage_at</c> (a SQL <c>MAX</c> against the existing value) rather than overwriting it
    /// unconditionally, so an out-of-order call - concurrent requests completing in a different order than
    /// they're recorded, a clock adjustment, delayed telemetry - can't move it backwards. Relies on the
    /// shared timestamp format being fixed-width, so lexicographic <c>TEXT</c> comparison agrees with
    /// chronological order.
    /// </summary>
    public void AddProviderSpend(
        string providerKey,
        string period,
        decimal costUsd,
        long promptTokens,
        long completionTokens,
        long cacheCreationTokens,
        long cacheReadTokens,
        DateTimeOffset usageAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(period);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        decimal existingCost = 0m;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT cost_usd FROM provider_spend WHERE provider_key = $key AND period = $period;";
            read.Parameters.AddWithValue("$key", providerKey);
            read.Parameters.AddWithValue("$period", period);
            if (read.ExecuteScalar() is string existing)
            {
                existingCost = decimal.Parse(existing, CultureInfo.InvariantCulture);
            }
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO provider_spend (
                    provider_key, period, cost_usd, prompt_tokens, completion_tokens,
                    cache_creation_tokens, cache_read_tokens, last_usage_at)
                VALUES ($key, $period, $cost, $prompt, $completion, $cacheCreation, $cacheRead, $usageAt)
                ON CONFLICT(provider_key, period) DO UPDATE SET
                    cost_usd              = $cost,
                    prompt_tokens         = prompt_tokens + excluded.prompt_tokens,
                    completion_tokens     = completion_tokens + excluded.completion_tokens,
                    cache_creation_tokens = cache_creation_tokens + excluded.cache_creation_tokens,
                    cache_read_tokens     = cache_read_tokens + excluded.cache_read_tokens,
                    last_usage_at         = MAX(COALESCE(last_usage_at, ''), excluded.last_usage_at);
                """;
            upsert.Parameters.AddWithValue("$key", providerKey);
            upsert.Parameters.AddWithValue("$period", period);
            upsert.Parameters.AddWithValue("$cost", (existingCost + costUsd).ToString(CultureInfo.InvariantCulture));
            upsert.Parameters.AddWithValue("$prompt", promptTokens);
            upsert.Parameters.AddWithValue("$completion", completionTokens);
            upsert.Parameters.AddWithValue("$cacheCreation", cacheCreationTokens);
            upsert.Parameters.AddWithValue("$cacheRead", cacheReadTokens);
            upsert.Parameters.AddWithValue("$usageAt", usageAtUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
