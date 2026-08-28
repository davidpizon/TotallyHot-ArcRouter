using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="UsageRollupStore"/>: the roll-forward/backfill checkpoint, the
/// never-publish-the-in-progress-bucket rule, grouped queries, and bucket math across a pinned timezone
/// (including a DST boundary).
/// </summary>
public class UsageRollupStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UsageLedgerEntry MakeEntry(
        string sessionId = "sess-1",
        string provider = "openai",
        string model = "gpt-4o",
        DateTimeOffset? occurredAtUtc = null,
        int promptTokens = 100,
        int completionTokens = 50,
        decimal? costUsd = 1.5m,
        string? requestId = null) =>
        new(
            SessionId: sessionId,
            TurnNumber: 1,
            Provider: provider,
            RequestedModel: model,
            ResolvedModel: model,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            CacheCreationTokens: null,
            CacheReadTokens: null,
            EstimatedCostUsd: costUsd,
            CostConfidence: costUsd is null ? CostConfidence.Unknown : CostConfidence.Catalog,
            OccurredAtUtc: occurredAtUtc ?? DateTimeOffset.UtcNow,
            RequestId: requestId ?? Guid.NewGuid().ToString("N"));

    // Anchors a test's entries to midday rather than to the current time-of-day. Tests that record a
    // second entry a few minutes after the first and then assert both landed in the *same* P1D bucket are
    // otherwise clock-dependent: "now" plus those minutes crosses midnight during the last few minutes of
    // a UTC day, splitting the entries across two day buckets. The query window (anchored to the first
    // entry's date) then sees only the earlier bucket, and the accumulated totals come up short - a real
    // failure that reproduces for only a few minutes out of every 24h. Midday leaves ~12h of headroom on
    // either side, so the offsets these tests add can never reach a bucket boundary.
    private static DateTimeOffset MiddayDaysAgo(int days) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-days).AddHours(12);

    [Fact]
    public async Task RollForward_AppliesLedgerEntry_QueryableAfterBucketCompletes()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = DateTimeOffset.UtcNow.AddDays(-2);
        await ledger.RecordAsync(MakeEntry(occurredAtUtc: occurredAt, promptTokens: 100, completionTokens: 50, costUsd: 1.5m), Ct);

        var dayStart = new DateTimeOffset(occurredAt.Date, TimeSpan.Zero);
        var results = rollup.Query(dayStart, dayStart.AddDays(1), "P1D", "model");

        var bucket = Assert.Single(results);
        Assert.Equal("gpt-4o", bucket.GroupKey);
        Assert.Equal(1, bucket.Requests);
        Assert.Equal(0, bucket.UnpricedRequests);
        Assert.Equal(100, bucket.PromptTokens);
        Assert.Equal(50, bucket.CompletionTokens);
        Assert.Equal(1.5m, bucket.CostUsd);
    }

    [Fact]
    public async Task RollForward_UnpricedEntry_CountsInUnpricedRequestsNotCost()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = DateTimeOffset.UtcNow.AddDays(-2);
        await ledger.RecordAsync(MakeEntry(occurredAtUtc: occurredAt, costUsd: null), Ct);

        var dayStart = new DateTimeOffset(occurredAt.Date, TimeSpan.Zero);
        var bucket = Assert.Single(rollup.Query(dayStart, dayStart.AddDays(1), "P1D", "model"));

        Assert.Equal(1, bucket.UnpricedRequests);
        Assert.Equal(0m, bucket.CostUsd);
    }

    [Fact]
    public async Task RollForward_CalledAgainWithNoNewEntries_ProcessesNothing()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        await ledger.RecordAsync(MakeEntry(occurredAtUtc: DateTimeOffset.UtcNow.AddDays(-2)), Ct);

        var second = await rollup.RollForwardAsync(Ct);

        Assert.Equal(0, second);
    }

    [Fact]
    public async Task RollForward_TwoEntriesSameBucket_AccumulatesRatherThanOverwrites()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = MiddayDaysAgo(2);
        await ledger.RecordAsync(MakeEntry(occurredAtUtc: occurredAt, promptTokens: 100, completionTokens: 50, costUsd: 1.5m), Ct);
        await ledger.RecordAsync(MakeEntry(occurredAtUtc: occurredAt.AddMinutes(1), promptTokens: 10, completionTokens: 5, costUsd: 0.25m), Ct);

        var dayStart = new DateTimeOffset(occurredAt.Date, TimeSpan.Zero);
        var bucket = Assert.Single(rollup.Query(dayStart, dayStart.AddDays(1), "P1D", "model"));

        Assert.Equal(2, bucket.Requests);
        Assert.Equal(110, bucket.PromptTokens);
        Assert.Equal(55, bucket.CompletionTokens);
        Assert.Equal(1.75m, bucket.CostUsd);
    }

    [Fact]
    public async Task RollForward_EntriesWrittenDirectlyToLedger_AreCaughtUpByBackfill()
    {
        // Simulates "buckets missed while down": entries land in usage_ledger (e.g. via a UsageLedger
        // instance with no rollup store wired up) and only a later RollForwardAsync call - the startup
        // backfill - rolls them up.
        using var temp = new TempDatabase();
        var ledgerOnly = temp.CreateUsageLedger(rollupStore: null);
        var occurredAt = MiddayDaysAgo(2);
        await ledgerOnly.RecordAsync(MakeEntry(occurredAtUtc: occurredAt), Ct);
        await ledgerOnly.RecordAsync(MakeEntry(occurredAtUtc: occurredAt.AddMinutes(5)), Ct);

        var rollup = temp.CreateRollupStore();
        var processed = await rollup.RollForwardAsync(Ct);

        Assert.Equal(2, processed);
        var dayStart = new DateTimeOffset(occurredAt.Date, TimeSpan.Zero);
        var bucket = Assert.Single(rollup.Query(dayStart, dayStart.AddDays(1), "P1D", "model"));
        Assert.Equal(2, bucket.Requests);

        // Re-running finds nothing new: the checkpoint already covers both entries.
        Assert.Equal(0, await rollup.RollForwardAsync(Ct));
    }

    [Fact]
    public async Task Query_ExcludesBucketThatHasNotFullyElapsedYet()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        // "Now" - today's P1D bucket cannot have fully elapsed.
        await ledger.RecordAsync(MakeEntry(occurredAtUtc: DateTimeOffset.UtcNow), Ct);

        var todayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var results = rollup.Query(todayStart, todayStart.AddDays(1), "P1D", "model");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Summary_SumsAcrossModelsAndProviders()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = DateTimeOffset.UtcNow.AddDays(-3);
        await ledger.RecordAsync(MakeEntry(provider: "openai", model: "gpt-4o", occurredAtUtc: occurredAt, costUsd: 1m), Ct);
        await ledger.RecordAsync(MakeEntry(provider: "anthropic", model: "claude", occurredAtUtc: occurredAt.AddMinutes(1), costUsd: 2m), Ct);

        var summary = rollup.Summary(occurredAt.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(2, summary.Requests);
        Assert.Equal(3m, summary.CostUsd);
    }

    [Fact]
    public async Task Query_GroupByProvider_AggregatesAcrossModels()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = MiddayDaysAgo(2);
        await ledger.RecordAsync(MakeEntry(provider: "openai", model: "gpt-4o", occurredAtUtc: occurredAt, costUsd: 1m), Ct);
        await ledger.RecordAsync(MakeEntry(provider: "openai", model: "gpt-4o-mini", occurredAtUtc: occurredAt.AddMinutes(1), costUsd: 0.5m), Ct);

        var dayStart = new DateTimeOffset(occurredAt.Date, TimeSpan.Zero);
        var bucket = Assert.Single(rollup.Query(dayStart, dayStart.AddDays(1), "P1D", "provider"));

        Assert.Equal("openai", bucket.GroupKey);
        Assert.Equal(2, bucket.Requests);
        Assert.Equal(1.5m, bucket.CostUsd);
    }

    [Fact]
    public async Task RollForward_DstTransition_DoesNotThrowAndProducesABucket()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore(rollupTimezone: "America/New_York");
        var ledger = temp.CreateUsageLedger(rollup);

        // 2026-03-08 07:30 UTC is 02:30 America/New_York on the US spring-forward date - a local wall-clock
        // time that never occurs in that timezone.
        var occurredAt = new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero);
        await ledger.RecordAsync(MakeEntry(occurredAtUtc: occurredAt), Ct);

        var results = rollup.Query(
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero),
            "P1D",
            "day");

        Assert.Single(results);
    }
}
