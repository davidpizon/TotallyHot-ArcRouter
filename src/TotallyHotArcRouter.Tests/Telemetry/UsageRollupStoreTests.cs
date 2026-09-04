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
        string? requestId = null)
    {
        return new UsageLedgerEntry(
            SessionId: sessionId,
            1,
            Provider: provider,
            RequestedModel: model,
            ResolvedModel: model,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            null,
            null,
            EstimatedCostUsd: costUsd,
            CostConfidence: costUsd is null ? CostConfidence.Unknown : CostConfidence.Catalog,
            OccurredAtUtc: occurredAtUtc ?? DateTimeOffset.UtcNow,
            RequestId: requestId ?? Guid.NewGuid().ToString("N"));
    }

    // Anchors a test's entries to midday rather than to the current time-of-day. Tests that record a
    // second entry a few minutes after the first and then assert both landed in the *same* P1D bucket are
    // otherwise clock-dependent: "now" plus those minutes crosses midnight during the last few minutes of
    // a UTC day, splitting the entries across two day buckets. The query window (anchored to the first
    // entry's date) then sees only the earlier bucket, and the accumulated totals come up short - a real
    // failure that reproduces for only a few minutes out of every 24h. Midday leaves ~12h of headroom on
    // either side, so the offsets these tests add can never reach a bucket boundary.
    private static DateTimeOffset MiddayDaysAgo(int days)
    {
        return new DateTimeOffset(dateTime: DateTime.UtcNow.Date, offset: TimeSpan.Zero).AddDays(-days).AddHours(12);
    }

    [Fact]
    public async Task RollForward_AppliesLedgerEntry_QueryableAfterBucketCompletes()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = DateTimeOffset.UtcNow.AddDays(-2);
        await ledger.RecordAsync(
            entry: MakeEntry(occurredAtUtc: occurredAt, promptTokens: 100, completionTokens: 50, costUsd: 1.5m),
            cancellationToken: Ct);

        var dayStart = new DateTimeOffset(dateTime: occurredAt.Date, offset: TimeSpan.Zero);
        var results = rollup.Query(from: dayStart, to: dayStart.AddDays(1), bucketWidth: "P1D", groupBy: "model");

        var bucket = Assert.Single(results);
        Assert.Equal(expected: "gpt-4o", actual: bucket.GroupKey);
        Assert.Equal(1, actual: bucket.Requests);
        Assert.Equal(0, actual: bucket.UnpricedRequests);
        Assert.Equal(100, actual: bucket.PromptTokens);
        Assert.Equal(50, actual: bucket.CompletionTokens);
        Assert.Equal(1.5m, actual: bucket.CostUsd);
    }

    [Fact]
    public async Task RollForward_UnpricedEntry_CountsInUnpricedRequestsNotCost()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = DateTimeOffset.UtcNow.AddDays(-2);
        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: occurredAt, costUsd: null), cancellationToken: Ct);

        var dayStart = new DateTimeOffset(dateTime: occurredAt.Date, offset: TimeSpan.Zero);
        var bucket = Assert.Single(rollup.Query(from: dayStart, to: dayStart.AddDays(1), bucketWidth: "P1D",
            groupBy: "model"));

        Assert.Equal(1, actual: bucket.UnpricedRequests);
        Assert.Equal(0m, actual: bucket.CostUsd);
    }

    [Fact]
    public async Task RollForward_CalledAgainWithNoNewEntries_ProcessesNothing()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: DateTimeOffset.UtcNow.AddDays(-2)),
            cancellationToken: Ct);

        var second = await rollup.RollForwardAsync(Ct);

        Assert.Equal(0, actual: second);
    }

    [Fact]
    public async Task RollForward_TwoEntriesSameBucket_AccumulatesRatherThanOverwrites()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = MiddayDaysAgo(2);
        await ledger.RecordAsync(
            entry: MakeEntry(occurredAtUtc: occurredAt, promptTokens: 100, completionTokens: 50, costUsd: 1.5m),
            cancellationToken: Ct);
        await ledger.RecordAsync(
            entry: MakeEntry(occurredAtUtc: occurredAt.AddMinutes(1), promptTokens: 10, completionTokens: 5,
                costUsd: 0.25m), cancellationToken: Ct);

        var dayStart = new DateTimeOffset(dateTime: occurredAt.Date, offset: TimeSpan.Zero);
        var bucket = Assert.Single(rollup.Query(from: dayStart, to: dayStart.AddDays(1), bucketWidth: "P1D",
            groupBy: "model"));

        Assert.Equal(2, actual: bucket.Requests);
        Assert.Equal(110, actual: bucket.PromptTokens);
        Assert.Equal(55, actual: bucket.CompletionTokens);
        Assert.Equal(1.75m, actual: bucket.CostUsd);
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
        await ledgerOnly.RecordAsync(entry: MakeEntry(occurredAtUtc: occurredAt), cancellationToken: Ct);
        await ledgerOnly.RecordAsync(entry: MakeEntry(occurredAtUtc: occurredAt.AddMinutes(5)), cancellationToken: Ct);

        var rollup = temp.CreateRollupStore();
        var processed = await rollup.RollForwardAsync(Ct);

        Assert.Equal(2, actual: processed);
        var dayStart = new DateTimeOffset(dateTime: occurredAt.Date, offset: TimeSpan.Zero);
        var bucket = Assert.Single(rollup.Query(from: dayStart, to: dayStart.AddDays(1), bucketWidth: "P1D",
            groupBy: "model"));
        Assert.Equal(2, actual: bucket.Requests);

        // Re-running finds nothing new: the checkpoint already covers both entries.
        Assert.Equal(0, actual: await rollup.RollForwardAsync(Ct));
    }

    [Fact]
    public async Task Query_ExcludesBucketThatHasNotFullyElapsedYet()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        // "Now" - today's P1D bucket cannot have fully elapsed.
        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: DateTimeOffset.UtcNow), cancellationToken: Ct);

        var todayStart = new DateTimeOffset(dateTime: DateTime.UtcNow.Date, offset: TimeSpan.Zero);
        var results = rollup.Query(from: todayStart, to: todayStart.AddDays(1), bucketWidth: "P1D", groupBy: "model");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Summary_SumsAcrossModelsAndProviders()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = DateTimeOffset.UtcNow.AddDays(-3);
        await ledger.RecordAsync(
            entry: MakeEntry(provider: "openai", model: "gpt-4o", occurredAtUtc: occurredAt, costUsd: 1m),
            cancellationToken: Ct);
        await ledger.RecordAsync(
            entry: MakeEntry(provider: "anthropic", model: "claude", occurredAtUtc: occurredAt.AddMinutes(1),
                costUsd: 2m), cancellationToken: Ct);

        var summary = rollup.Summary(from: occurredAt.AddDays(-1), to: DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(2, actual: summary.Requests);
        Assert.Equal(3m, actual: summary.CostUsd);
    }

    [Fact]
    public async Task Query_GroupByProvider_AggregatesAcrossModels()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);

        var occurredAt = MiddayDaysAgo(2);
        await ledger.RecordAsync(
            entry: MakeEntry(provider: "openai", model: "gpt-4o", occurredAtUtc: occurredAt, costUsd: 1m),
            cancellationToken: Ct);
        await ledger.RecordAsync(
            entry: MakeEntry(provider: "openai", model: "gpt-4o-mini", occurredAtUtc: occurredAt.AddMinutes(1),
                costUsd: 0.5m), cancellationToken: Ct);

        var dayStart = new DateTimeOffset(dateTime: occurredAt.Date, offset: TimeSpan.Zero);
        var bucket = Assert.Single(rollup.Query(from: dayStart, to: dayStart.AddDays(1), bucketWidth: "P1D",
            groupBy: "provider"));

        Assert.Equal(expected: "openai", actual: bucket.GroupKey);
        Assert.Equal(2, actual: bucket.Requests);
        Assert.Equal(1.5m, actual: bucket.CostUsd);
    }

    [Fact]
    public async Task RollForward_DstTransition_DoesNotThrowAndProducesABucket()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore(rollupTimezone: "America/New_York");
        var ledger = temp.CreateUsageLedger(rollup);

        // 2026-03-08 07:30 UTC is 02:30 America/New_York on the US spring-forward date - a local wall-clock
        // time that never occurs in that timezone.
        var occurredAt = new DateTimeOffset(2026, 3, 8, 7, 30, 0, offset: TimeSpan.Zero);
        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: occurredAt), cancellationToken: Ct);

        var results = rollup.Query(
            from: new DateTimeOffset(2026, 3, 1, 0, 0, 0, offset: TimeSpan.Zero),
            to: new DateTimeOffset(2026, 3, 15, 0, 0, 0, offset: TimeSpan.Zero),
            bucketWidth: "P1D",
            groupBy: "day");

        Assert.Single(results);
    }
}