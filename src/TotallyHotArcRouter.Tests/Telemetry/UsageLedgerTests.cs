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
        decimal? estimatedCostUsd = 0.01m) =>
        new(
            SessionId: sessionId,
            TurnNumber: turnNumber,
            Provider: provider,
            RequestedModel: requestedModel,
            ResolvedModel: requestedModel,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            CacheCreationTokens: null,
            CacheReadTokens: null,
            EstimatedCostUsd: estimatedCostUsd,
            CostConfidence: CostConfidence.Unknown,
            OccurredAtUtc: occurredAtUtc ?? DateTimeOffset.UtcNow,
            RequestId: requestId);

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

        await ledger.RecordAsync(MakeEntry(), TestContext.Current.CancellationToken);

        Assert.Equal(1, CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_ReplayedRequestId_DoesNotDoubleCount()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();
        var entry = MakeEntry(requestId: "req-abc-123");

        await ledger.RecordAsync(entry, TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry, TestContext.Current.CancellationToken);
        await ledger.RecordAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal(1, CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_RequestIdPreferredOverComposite_DifferentTokenCountsStillDedupe()
    {
        // With a request id present, the composite fields (which differ here) must not matter - the
        // request id alone determines the dedup key.
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(MakeEntry(requestId: "req-xyz", promptTokens: 10, completionTokens: 5), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(MakeEntry(requestId: "req-xyz", promptTokens: 999, completionTokens: 999), TestContext.Current.CancellationToken);

        Assert.Equal(1, CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_CompositeKey_StableAcrossReparseWithinSameSecond()
    {
        // Two calls describing the same request, timestamped a few ticks apart within the same second (the
        // sub-second jitter a retried telemetry publish could introduce), must still collide.
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        await ledger.RecordAsync(MakeEntry(occurredAtUtc: baseTime), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(MakeEntry(occurredAtUtc: baseTime.AddMilliseconds(400)), TestContext.Current.CancellationToken);

        Assert.Equal(1, CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_CompositeKey_DifferentSecondsProduceDistinctRows()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        await ledger.RecordAsync(MakeEntry(occurredAtUtc: baseTime), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(MakeEntry(occurredAtUtc: baseTime.AddSeconds(1)), TestContext.Current.CancellationToken);

        Assert.Equal(2, CountRows(temp));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(5, -1)]
    public async Task RecordAsync_NegativeTokenCount_IsDroppedNotWritten(int? promptTokens, int? completionTokens)
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(MakeEntry(promptTokens: promptTokens, completionTokens: completionTokens), TestContext.Current.CancellationToken);

        Assert.Equal(0, CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_NegativeCost_IsDroppedNotWritten()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(MakeEntry(estimatedCostUsd: -0.5m), TestContext.Current.CancellationToken);

        Assert.Equal(0, CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_FarFutureTimestamp_IsDroppedNotWritten()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(MakeEntry(occurredAtUtc: DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);

        Assert.Equal(0, CountRows(temp));
    }

    [Fact]
    public async Task RecordAsync_SlightFutureTimestampWithinClockSkewTolerance_IsWritten()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(MakeEntry(occurredAtUtc: DateTimeOffset.UtcNow.AddMinutes(1)), TestContext.Current.CancellationToken);

        Assert.Equal(1, CountRows(temp));
    }

    [Fact]
    public void GetMaxTurnNumber_NoEntries_ReturnsZero()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        Assert.Equal(0, ledger.GetMaxTurnNumber("unknown-session"));
    }

    [Fact]
    public async Task GetMaxTurnNumber_ReturnsHighestRecordedTurnForSession()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();

        await ledger.RecordAsync(MakeEntry(sessionId: "sess-a", turnNumber: 1, requestId: "req-1"), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(MakeEntry(sessionId: "sess-a", turnNumber: 2, requestId: "req-2"), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(MakeEntry(sessionId: "sess-a", turnNumber: 3, requestId: "req-3"), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(MakeEntry(sessionId: "sess-b", turnNumber: 7, requestId: "req-4"), TestContext.Current.CancellationToken);

        Assert.Equal(3, ledger.GetMaxTurnNumber("sess-a"));
        Assert.Equal(7, ledger.GetMaxTurnNumber("sess-b"));
    }

    [Fact]
    public async Task DeleteOlderThan_DeletesOnlyRowsStrictlyBeforeCutoff()
    {
        using var temp = new TempDatabase();
        var ledger = temp.CreateUsageLedger();
        var cutoff = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        await ledger.RecordAsync(MakeEntry(requestId: "old", occurredAtUtc: cutoff.AddDays(-1)), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(MakeEntry(requestId: "boundary", occurredAtUtc: cutoff), TestContext.Current.CancellationToken);
        await ledger.RecordAsync(MakeEntry(requestId: "new", occurredAtUtc: cutoff.AddDays(1)), TestContext.Current.CancellationToken);

        var deleted = ledger.DeleteOlderThan(cutoff);

        Assert.Equal(1, deleted);
        Assert.Equal(2, CountRows(temp));
    }
}
