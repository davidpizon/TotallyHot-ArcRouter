using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="UsageLedger"/>.</summary>
public class UsageLedgerTests
{
    private static UsageLedgerEntry MakeEntry(
        string sessionId = "sess-1",
        int turnNumber = 1,
        string provider = "openai",
        string requestedModel = "gpt-4o",
        string? requestId = null,
        DateTimeOffset? occurredAtUtc = null,
        int? promptTokens = 10,
        int? completionTokens = 5,
        decimal? estimatedCostUsd = 0.01m)
    {
        return new UsageLedgerEntry(
            SessionId: sessionId,
            TurnNumber: turnNumber,
            Provider: provider,
            RequestedModel: requestedModel,
            ResolvedModel: requestedModel,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            null,
            null,
            EstimatedCostUsd: estimatedCostUsd,
            CostConfidence: CostConfidence.Unknown,
            OccurredAtUtc: occurredAtUtc ?? DateTimeOffset.UtcNow,
            RequestId: requestId);
    }

    private static int CountRows(TempDatabase temp)
    {
        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_ledger;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public async Task RecordAsync_ValidEntry_WritesOneRow()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(entry: MakeEntry(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_ReplayedRequestId_DoesNotDoubleCount()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();
        var entry = MakeEntry(requestId: "req-abc-123");

        await ledger.RecordAsync(entry: entry, cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: entry, cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: entry, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_RequestIdPreferredOverComposite_DifferentTokenCountsStillDedupe()
    {
        // With a request id present, the composite fields (which differ here) must not matter - the
        // request id alone determines the dedup key.
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(entry: MakeEntry(requestId: "req-xyz", promptTokens: 10, completionTokens: 5),
            cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: MakeEntry(requestId: "req-xyz", promptTokens: 999, completionTokens: 999),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_CompositeKey_StableAcrossReparseWithinSameSecond()
    {
        // Two calls describing the same request, timestamped a few ticks apart within the same second (the
        // sub-second jitter a retried telemetry publish could introduce), must still collide.
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, offset: TimeSpan.Zero);

        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: baseTime),
            cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: baseTime.AddMilliseconds(400)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_CompositeKey_DifferentSecondsProduceDistinctRows()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, offset: TimeSpan.Zero);

        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: baseTime),
            cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: baseTime.AddSeconds(1)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, actual: CountRows(temp));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(5, -1)]
    public async Task RecordAsync_NegativeTokenCount_IsDroppedNotWritten(int? promptTokens, int? completionTokens)
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(entry: MakeEntry(promptTokens: promptTokens, completionTokens: completionTokens),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_NegativeCost_IsDroppedNotWritten()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(entry: MakeEntry(estimatedCostUsd: -0.5m),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_FarFutureTimestamp_IsDroppedNotWritten()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: DateTimeOffset.UtcNow.AddHours(1)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_SlightFutureTimestampWithinClockSkewTolerance_IsWritten()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(entry: MakeEntry(occurredAtUtc: DateTimeOffset.UtcNow.AddMinutes(1)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: CountRows(temp));
    }

    [Fact]
    public void GetMaxTurnNumber_NoEntries_ReturnsZero()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        Assert.Equal(0, actual: ledger.GetMaxTurnNumber("unknown-session"));
    }

    [Fact]
    public async Task GetMaxTurnNumber_ReturnsHighestRecordedTurnForSession()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(entry: MakeEntry(sessionId: "sess-a", 1, requestId: "req-1"),
            cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: MakeEntry(sessionId: "sess-a", 2, requestId: "req-2"),
            cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: MakeEntry(sessionId: "sess-a", 3, requestId: "req-3"),
            cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: MakeEntry(sessionId: "sess-b", 7, requestId: "req-4"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, actual: ledger.GetMaxTurnNumber("sess-a"));
        Assert.Equal(7, actual: ledger.GetMaxTurnNumber("sess-b"));
    }

    [Fact]
    public async Task DeleteOlderThan_DeletesOnlyRowsStrictlyBeforeCutoff()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();
        var cutoff = new DateTimeOffset(2026, 1, 10, 0, 0, 0, offset: TimeSpan.Zero);

        await ledger.RecordAsync(entry: MakeEntry(requestId: "old", occurredAtUtc: cutoff.AddDays(-1)),
            cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: MakeEntry(requestId: "boundary", occurredAtUtc: cutoff),
            cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry: MakeEntry(requestId: "new", occurredAtUtc: cutoff.AddDays(1)),
            cancellationToken: TestContext.Current.CancellationToken);

        var deleted = ledger.DeleteOlderThan(cutoff);

        Assert.Equal(1, actual: deleted);
        Assert.Equal(2, actual: CountRows(temp));
    }

    [Fact]
    public async Task SumEstimatedCostUsd_SumsOnlyMatchingProviderWithinWindow()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();
        var windowStart = new DateTimeOffset(2026, 1, 10, 0, 0, 0, offset: TimeSpan.Zero);
        var windowEnd = windowStart.AddDays(1);

        await ledger.RecordAsync(
            entry: MakeEntry(requestId: "in-window-openai-1", provider: "openai", estimatedCostUsd: 1.25m,
                occurredAtUtc: windowStart), cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(
            entry: MakeEntry(requestId: "in-window-openai-2", provider: "openai", estimatedCostUsd: 2.50m,
                occurredAtUtc: windowStart.AddHours(12)), cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(
            entry: MakeEntry(requestId: "in-window-anthropic", provider: "anthropic", estimatedCostUsd: 99m,
                occurredAtUtc: windowStart.AddHours(1)), cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(
            entry: MakeEntry(requestId: "before-window", provider: "openai", estimatedCostUsd: 100m,
                occurredAtUtc: windowStart.AddSeconds(-1)), cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(
            entry: MakeEntry(requestId: "at-window-end", provider: "openai", estimatedCostUsd: 100m,
                occurredAtUtc: windowEnd), cancellationToken: TestContext.Current.CancellationToken);
        await ledger.RecordAsync(
            entry: MakeEntry(requestId: "no-cost", provider: "openai", estimatedCostUsd: null,
                occurredAtUtc: windowStart.AddHours(2)), cancellationToken: TestContext.Current.CancellationToken);

        var total = ledger.SumEstimatedCostUsd(provider: "openai", fromUtc: windowStart, toUtc: windowEnd);

        Assert.Equal(3.75m, actual: total);
    }

    [Fact]
    public void SumEstimatedCostUsd_NoMatchingRows_ReturnsZero()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        var total = ledger.SumEstimatedCostUsd(provider: "openai", fromUtc: DateTimeOffset.UtcNow.AddDays(-1),
            toUtc: DateTimeOffset.UtcNow);

        Assert.Equal(0m, actual: total);
    }
}