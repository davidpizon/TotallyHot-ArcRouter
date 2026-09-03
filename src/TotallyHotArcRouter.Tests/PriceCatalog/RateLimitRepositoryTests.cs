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

        repository.UpsertRateLimitHeaders(providerKey: "anthropic", headers: [], observedAtUtc: DateTimeOffset.UtcNow);

        var (headers, observedAt) = repository.GetRateLimitSnapshot("anthropic");
        Assert.Empty(headers);
        Assert.Null(observedAt);
    }

    [Fact]
    public void UpsertRateLimitHeaders_UpsertsLatestValuePerHeader()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var first = new DateTimeOffset(2026, 3, 1, 12, 0, 0, offset: TimeSpan.Zero);
        var second = first.AddMinutes(1);

        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "1000")],
            observedAtUtc: first);
        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "500")],
            observedAtUtc: second);

        var (headers, observedAt) = repository.GetRateLimitSnapshot("anthropic");
        var row = Assert.Single(headers);
        Assert.Equal(expected: "500", actual: row.HeaderValue);
        Assert.Equal(expected: second, actual: observedAt);
    }

    [Fact]
    public void UpsertRateLimitHeaders_HeaderNameIsLowercased()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();

        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "Anthropic-Ratelimit-Requests-Limit", HeaderValue: "50")],
            observedAtUtc: DateTimeOffset.UtcNow);

        var (headers, _) = repository.GetRateLimitSnapshot("anthropic");
        Assert.Equal(expected: "anthropic-ratelimit-requests-limit", actual: Assert.Single(headers).HeaderName);
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
        var timestamp = new DateTimeOffset(2026, 3, 1, 12, 0, 30, offset: TimeSpan.Zero);
        var laterSameMinute = timestamp.AddSeconds(20);

        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "1000")],
            observedAtUtc: timestamp);
        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "999")],
            observedAtUtc: laterSameMinute);

        Assert.Equal(1, actual: CountHistoryRows(database: database, providerKey: "anthropic"));
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_AddsARowForANewMinuteBucket()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var database = temp.Database;
        var first = new DateTimeOffset(2026, 3, 1, 12, 0, 0, offset: TimeSpan.Zero);
        var nextMinute = first.AddMinutes(1);

        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "1000")],
            observedAtUtc: first);
        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "999")],
            observedAtUtc: nextMinute);

        Assert.Equal(2, actual: CountHistoryRows(database: database, providerKey: "anthropic"));
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_PrunesRowsOlderThan30Days()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var database = temp.Database;
        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, offset: TimeSpan.Zero);
        var recent = old.AddDays(31);

        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "1000")],
            observedAtUtc: old);
        Assert.Equal(1, actual: CountHistoryRows(database: database, providerKey: "anthropic"));

        // The write itself carries the pruning: a capture more than 30 days after the old row is what
        // triggers its removal, not a background job.
        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "999")],
            observedAtUtc: recent);

        Assert.Equal(1, actual: CountHistoryRows(database: database, providerKey: "anthropic"));
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_PruneIsScopedToTheWritingProvider()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var database = temp.Database;
        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, offset: TimeSpan.Zero);
        var recent = old.AddDays(31);

        // An old row for a different provider than the one about to write - the prune must not delete it
        // just because it's old; it should only ever touch the writing provider's own rows.
        repository.UpsertRateLimitHeaders(
            providerKey: "openai",
            headers: [new RateLimitHeaderRow(HeaderName: "x-ratelimit-remaining-requests", HeaderValue: "1000")],
            observedAtUtc: old);
        Assert.Equal(1, actual: CountHistoryRows(database: database, providerKey: "openai"));

        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "999")],
            observedAtUtc: recent);

        Assert.Equal(1, actual: CountHistoryRows(database: database, providerKey: "openai"));
    }

    [Fact]
    public void GetRateLimitHistory_ReturnsBucketsChronologicallyWithOnlyCapturedHeaders()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var first = new DateTimeOffset(2026, 3, 1, 12, 0, 0, offset: TimeSpan.Zero);
        var second = first.AddMinutes(1);

        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "1000")],
            observedAtUtc: first);
        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "900")],
            observedAtUtc: second);

        var buckets = repository.GetRateLimitHistory(providerKey: "anthropic", sinceUtc: first.AddMinutes(-5));

        Assert.Equal(2, actual: buckets.Count);
        Assert.Equal(expected: first, actual: buckets[0].BucketUtc);
        Assert.Single(buckets[0].Headers);
        Assert.Equal(expected: "1000", actual: buckets[0].Headers[0].HeaderValue);
        Assert.Equal(expected: second, actual: buckets[1].BucketUtc);
        Assert.Equal(expected: "900", actual: buckets[1].Headers[0].HeaderValue);
    }

    [Fact]
    public void GetRateLimitHistory_ExcludesBucketsBeforeSince()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var old = new DateTimeOffset(2026, 3, 1, 12, 0, 0, offset: TimeSpan.Zero);
        var recent = old.AddMinutes(10);

        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "1000")],
            observedAtUtc: old);
        repository.UpsertRateLimitHeaders(
            providerKey: "anthropic",
            headers: [new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "900")],
            observedAtUtc: recent);

        var buckets = repository.GetRateLimitHistory(providerKey: "anthropic", sinceUtc: old.AddMinutes(5));

        Assert.Single(buckets);
        Assert.Equal(expected: recent, actual: buckets[0].BucketUtc);
    }

    [Fact]
    public void GetRateLimitHistory_UnknownProvider_ReturnsEmpty()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();

        var buckets =
            repository.GetRateLimitHistory(providerKey: "does-not-exist", sinceUtc: DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Empty(buckets);
    }

    private static int CountHistoryRows(PriceCatalogDatabase database, string providerKey)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_rate_limit_history WHERE provider_key = $key;";
        command.Parameters.AddWithValue(parameterName: "$key", value: providerKey);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}