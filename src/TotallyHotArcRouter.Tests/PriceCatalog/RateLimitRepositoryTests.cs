using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="RateLimitRepository"/>'s header snapshot and minute-bucketed history.</summary>
public class RateLimitRepositoryTests
{
    [Fact]
    public void UpsertRateLimitHeaders_EmptyList_IsNoOp()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();

        repository.UpsertRateLimitHeaders("anthropic", [], DateTimeOffset.UtcNow);

        var (headers, observedAt) = repository.GetRateLimitSnapshot("anthropic");
        Assert.Empty(headers);
        Assert.Null(observedAt);
    }

    [Fact]
    public void UpsertRateLimitHeaders_UpsertsLatestValuePerHeader()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var first = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddMinutes(1);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            first);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "500")],
            second);

        var (headers, observedAt) = repository.GetRateLimitSnapshot("anthropic");
        var row = Assert.Single(headers);
        Assert.Equal("500", row.HeaderValue);
        Assert.Equal(second, observedAt);
    }

    [Fact]
    public void UpsertRateLimitHeaders_HeaderNameIsLowercased()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("Anthropic-Ratelimit-Requests-Limit", "50")],
            DateTimeOffset.UtcNow);

        var (headers, _) = repository.GetRateLimitSnapshot("anthropic");
        Assert.Equal("anthropic-ratelimit-requests-limit", Assert.Single(headers).HeaderName);
    }

    [Fact]
    public void GetRateLimitSnapshot_UnknownProvider_ReturnsEmptyAndNullObservedAt()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();

        var (headers, observedAt) = repository.GetRateLimitSnapshot("does-not-exist");

        Assert.Empty(headers);
        Assert.Null(observedAt);
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_DedupesWithinTheSameMinuteBucket()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var database = temp.Database;
        var timestamp = new DateTimeOffset(2026, 3, 1, 12, 0, 30, TimeSpan.Zero);
        var laterSameMinute = timestamp.AddSeconds(20);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            timestamp);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "999")],
            laterSameMinute);

        Assert.Equal(1, CountHistoryRows(database, "anthropic"));
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_AddsARowForANewMinuteBucket()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var database = temp.Database;
        var first = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var nextMinute = first.AddMinutes(1);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            first);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "999")],
            nextMinute);

        Assert.Equal(2, CountHistoryRows(database, "anthropic"));
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_PrunesRowsOlderThan30Days()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var database = temp.Database;
        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recent = old.AddDays(31);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            old);
        Assert.Equal(1, CountHistoryRows(database, "anthropic"));

        // The write itself carries the pruning: a capture more than 30 days after the old row is what
        // triggers its removal, not a background job.
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "999")],
            recent);

        Assert.Equal(1, CountHistoryRows(database, "anthropic"));
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_PruneIsScopedToTheWritingProvider()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var database = temp.Database;
        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recent = old.AddDays(31);

        // An old row for a different provider than the one about to write - the prune must not delete it
        // just because it's old; it should only ever touch the writing provider's own rows.
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("x-ratelimit-remaining-requests", "1000")],
            old);
        Assert.Equal(1, CountHistoryRows(database, "openai"));

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "999")],
            recent);

        Assert.Equal(1, CountHistoryRows(database, "openai"));
    }

    [Fact]
    public void GetRateLimitHistory_ReturnsBucketsChronologicallyWithOnlyCapturedHeaders()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var first = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddMinutes(1);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            first);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "900")],
            second);

        var buckets = repository.GetRateLimitHistory("anthropic", first.AddMinutes(-5));

        Assert.Equal(2, buckets.Count);
        Assert.Equal(first, buckets[0].BucketUtc);
        Assert.Single(buckets[0].Headers);
        Assert.Equal("1000", buckets[0].Headers[0].HeaderValue);
        Assert.Equal(second, buckets[1].BucketUtc);
        Assert.Equal("900", buckets[1].Headers[0].HeaderValue);
    }

    [Fact]
    public void GetRateLimitHistory_ExcludesBucketsBeforeSince()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var old = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var recent = old.AddMinutes(10);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            old);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "900")],
            recent);

        var buckets = repository.GetRateLimitHistory("anthropic", old.AddMinutes(5));

        Assert.Single(buckets);
        Assert.Equal(recent, buckets[0].BucketUtc);
    }

    [Fact]
    public void GetRateLimitHistory_UnknownProvider_ReturnsEmpty()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();

        var buckets = repository.GetRateLimitHistory("does-not-exist", DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Empty(buckets);
    }

    private static int CountHistoryRows(PriceCatalogDatabase database, string providerKey)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_rate_limit_history WHERE provider_key = $key;";
        command.Parameters.AddWithValue("$key", providerKey);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
