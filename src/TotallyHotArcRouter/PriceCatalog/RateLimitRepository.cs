using System.Globalization;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Thin ADO.NET wrapper over the rate-limit header/history tables (<c>provider_rate_limit_snapshot</c> and
/// <c>provider_rate_limit_history</c>). Split out of the former monolithic <c>PriceCatalogRepository</c>
/// (docs/router/code-smell-refactoring-plan.md M3) - the other five concerns that repository once mixed
/// together each now have their own type in this namespace.
/// </summary>
public sealed class RateLimitRepository : PriceCatalogRepositoryBase
{
    // Minute-bucketed history is pruned back to this window on every write (TokenTracker's bounded-growth
    // lesson from the plan) - opportunistic, not a scheduled job, so an install that stops receiving
    // rate-limit headers simply stops growing the table rather than needing a cleanup task.
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(30);

    /// <summary>Initializes a new instance of the <see cref="RateLimitRepository"/> class.</summary>
    /// <param name="database">The catalog database.</param>
    public RateLimitRepository(PriceCatalogDatabase database)
        : base(database)
    {
    }

    /// <summary>
    /// Upserts one provider's captured <c>anthropic-ratelimit-*</c> response headers into the latest-value
    /// snapshot, and records at most one row per (provider, minute bucket, header) into the history table -
    /// a second capture within the same minute for a header already recorded this minute is a no-op there,
    /// enforced atomically by a unique index plus <c>INSERT ... ON CONFLICT DO NOTHING</c> so concurrent
    /// captures can't race past a check-then-insert and both land a row. A no-op when
    /// <paramref name="headers"/> is empty. Also prunes history rows older than 30 days.
    /// </summary>
    public void UpsertRateLimitHeaders(string providerKey, IReadOnlyList<RateLimitHeaderRow> headers, DateTimeOffset observedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.Count == 0)
        {
            return;
        }

        var observedAt = observedAtUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var minuteBucket = observedAtUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        var historyCutoff = (observedAtUtc - HistoryRetention).UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var header in headers)
        {
            using (var snapshot = connection.CreateCommand())
            {
                snapshot.Transaction = transaction;
                snapshot.CommandText = """
                    INSERT INTO provider_rate_limit_snapshot (provider_key, header_name, header_value, observed_at)
                    VALUES ($key, $name, $value, $observed)
                    ON CONFLICT(provider_key, header_name) DO UPDATE SET
                        header_value = excluded.header_value,
                        observed_at  = excluded.observed_at;
                    """;
                snapshot.Parameters.AddWithValue("$key", providerKey);
                snapshot.Parameters.AddWithValue("$name", header.HeaderName.ToLowerInvariant());
                snapshot.Parameters.AddWithValue("$value", header.HeaderValue);
                snapshot.Parameters.AddWithValue("$observed", observedAt);
                snapshot.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO provider_rate_limit_history (provider_key, minute_bucket, header_name, header_value)
                    VALUES ($key, $bucket, $name, $value)
                    ON CONFLICT(provider_key, minute_bucket, header_name) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$key", providerKey);
                insert.Parameters.AddWithValue("$bucket", minuteBucket);
                insert.Parameters.AddWithValue("$name", header.HeaderName.ToLowerInvariant());
                insert.Parameters.AddWithValue("$value", header.HeaderValue);
                insert.ExecuteNonQuery();
            }
        }

        // Scoped to this provider (not a global sweep) so SQLite can drive the delete off the leading edge
        // of ix_provider_rate_limit_history_dedupe's (provider_key, minute_bucket, header_name) index - a
        // range scan bounded to this provider's own rows - instead of a full-table scan on every capture,
        // which is what an unscoped WHERE minute_bucket < $cutoff would force as the table grows across
        // every provider.
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM provider_rate_limit_history WHERE provider_key = $key AND minute_bucket < $cutoff;";
            prune.Parameters.AddWithValue("$key", providerKey);
            prune.Parameters.AddWithValue("$cutoff", historyCutoff);
            prune.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Returns a provider's latest captured rate-limit header snapshot, along with the shared
    /// <c>observed_at</c> of the capture that produced it. Returns an empty header list and a
    /// <see langword="null"/> instant when no header has ever been captured for this provider.
    /// </summary>
    public (IReadOnlyList<RateLimitHeaderRow> Headers, DateTimeOffset? ObservedAtUtc) GetRateLimitSnapshot(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT header_name, header_value, observed_at
            FROM provider_rate_limit_snapshot
            WHERE provider_key = $key;
            """;
        command.Parameters.AddWithValue("$key", providerKey);

        // Every header captured together in one UpsertRateLimitHeaders call shares the same observed_at.
        // A header the provider stops sending keeps its old observed_at forever (nothing upserts it again),
        // so buffering every row first and filtering to only the latest observed_at - rather than returning
        // every row regardless of age - keeps the returned snapshot to headers from a single coherent
        // capture instead of mixing in a stale one. That matters for headers like OpenAI's
        // x-ratelimit-reset-*, a duration relative to the response that produced it, not to whatever the
        // newest unrelated header happens to be.
        var buffered = new List<(string Name, string Value, DateTimeOffset ObservedAt)>();
        DateTimeOffset? latest = null;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var observedAt = ParseTimestamp(reader.GetString(2));
                buffered.Add((reader.GetString(0), reader.GetString(1), observedAt));
                if (latest is null || observedAt > latest)
                {
                    latest = observedAt;
                }
            }
        }

        if (latest is null)
        {
            return ([], null);
        }

        var rows = new List<RateLimitHeaderRow>();
        foreach (var row in buffered)
        {
            if (row.ObservedAt == latest)
            {
                rows.Add(new RateLimitHeaderRow(row.Name, row.Value));
            }
        }

        return (rows, latest);
    }

    /// <summary>
    /// Returns a provider's captured rate-limit header history since <paramref name="sinceUtc"/>, grouped
    /// into minute buckets in chronological order (the shape <see cref="UpsertRateLimitHeaders"/> writes).
    /// Each bucket carries only the headers actually captured in that minute; a bucket with no captures is
    /// simply absent, never filled forward. Backs the Providers card's rate-limit trend chart and burn-rate
    /// projection (§5.9).
    /// </summary>
    public IReadOnlyList<RateLimitHistoryBucket> GetRateLimitHistory(string providerKey, DateTimeOffset sinceUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        var sinceBucket = sinceUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT minute_bucket, header_name, header_value
            FROM provider_rate_limit_history
            WHERE provider_key = $key AND minute_bucket >= $since
            ORDER BY minute_bucket;
            """;
        command.Parameters.AddWithValue("$key", providerKey);
        command.Parameters.AddWithValue("$since", sinceBucket);

        // Rows arrive pre-sorted by minute_bucket, so a run of rows sharing the same bucket string is
        // always contiguous - one pass groups them without a Dictionary's unordered enumeration risk.
        var buckets = new List<(string Bucket, List<RateLimitHeaderRow> Headers)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var bucket = reader.GetString(0);
                if (buckets.Count == 0 || buckets[^1].Bucket != bucket)
                {
                    buckets.Add((bucket, []));
                }

                buckets[^1].Headers.Add(new RateLimitHeaderRow(reader.GetString(1), reader.GetString(2)));
            }
        }

        return buckets
            .Select(b => new RateLimitHistoryBucket(ParseMinuteBucket(b.Bucket), b.Headers))
            .ToList();
    }

    // minute_bucket is stored as "yyyy-MM-ddTHH:mm" (see UpsertRateLimitHeaders); appending seconds and a
    // 'Z' turns it into an unambiguous UTC instant for ParseExact.
    /// <summary>Parses a stored <c>minute_bucket</c> string back into a UTC instant.</summary>
    /// <param name="minuteBucket">The bucket key, formatted <c>"yyyy-MM-ddTHH:mm"</c>.</param>
    /// <returns>The bucket start as a UTC <see cref="DateTimeOffset"/>.</returns>
    private static DateTimeOffset ParseMinuteBucket(string minuteBucket) =>
        DateTimeOffset.ParseExact(
            minuteBucket + ":00Z",
            "yyyy-MM-ddTHH:mm:ssK",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
}
