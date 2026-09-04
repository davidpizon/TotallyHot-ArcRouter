using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementReportingService.GetUsageSummary"/>,
/// <see cref="ManagementReportingService.GetUsageRollup"/>,
/// and <see cref="ManagementReportingService.GetRoutingRoiAsync"/> (Phase 4, §5.15;
/// docs/router/self-organizing-classification-plan.md Phase T4) - the read-only reporting surface split out
/// of <see cref="ManagementFacade"/> (docs/router/code-smell-refactoring-plan.md Phase 3 step 1).
/// </summary>
public sealed class ManagementReportingServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetRoutingRoiAsync_NoComparisonStore_IsUnavailableNotEmpty()
    {
        // "Unavailable" and "an empty range" are different answers: collapsing them would let a disabled
        // feature render as a measured break-even result.
        var service = new ManagementReportingService(null, null);
        var result = await service.GetRoutingRoiAsync(from: DateTimeOffset.UtcNow.AddDays(-1),
            to: DateTimeOffset.UtcNow, cancellationToken: Ct);

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.Unavailable, actual: result.ErrorType);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_InvertedRange_IsInvalidRequest()
    {
        var service = new ManagementReportingService(null, comparisonStore: new StubComparisonStore([]));
        var now = DateTimeOffset.UtcNow;

        var result = await service.GetRoutingRoiAsync(from: now, to: now.AddDays(-1), cancellationToken: Ct);

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.InvalidRequest, actual: result.ErrorType);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_ProjectsTheCostHalfAndExcludesRowsPastTheUpperBound()
    {
        var now = DateTimeOffset.UtcNow;
        var inRange = MakeComparison(1, comparedAt: now.AddHours(-2), 0.09m);
        var pastEnd = MakeComparison(2, comparedAt: now.AddHours(2), 0.50m);
        var service =
            new ManagementReportingService(null, comparisonStore: new StubComparisonStore([inRange, pastEnd]));

        var result = await service.GetRoutingRoiAsync(from: now.AddDays(-1), to: now, cancellationToken: Ct);

        Assert.True(result.Success);
        var point = Assert.Single(result.Value!);
        Assert.Equal(0.09m, actual: point.EstimatedNetSavingsUsd);
        Assert.Equal(expected: "kimi-k2.5", actual: point.RoutedModel);
        Assert.Equal(expected: "glm-5", actual: point.BaselineModel);
    }

    /// <summary>Builds a comparison row carrying a known savings figure at a known instant.</summary>
    private static TaxonomyComparisonRecord MakeComparison(
        long id, DateTimeOffset comparedAt, decimal savings)
    {
        return new TaxonomyComparisonRecord(
            TranscriptId: id,
            ComparedAtUtc: comparedAt,
            SessionId: "session-1",
            0.8,
            0.7,
            0.79,
            0.1,
            0.01,
            true,
            false,
            RoutedModel: "kimi-k2.5",
            BaselineModel: "glm-5",
            0.02m,
            BaselineEstimatedCostUsd: 0.02m + savings,
            EstimatedNetSavingsUsd: savings,
            0.75,
            -0.05);
    }

    [Fact]
    public void GetUsageSummary_NoRollupStore_IsUnavailable()
    {
        var service = new ManagementReportingService(null, null);
        var result = service.GetUsageSummary("day");

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.Unavailable, actual: result.ErrorType);
    }

    [Fact]
    public void GetUsageSummary_UnknownWindow_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var service = new ManagementReportingService(rollupStore: temp.CreateRollupStore(), null);

        var result = service.GetUsageSummary("fortnight");

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.InvalidRequest, actual: result.ErrorType);
    }

    [Fact]
    public async Task GetUsageSummary_WithData_ReturnsTotals()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);
        await ledger.RecordAsync(
            entry: new UsageLedgerEntry(
                SessionId: "sess-1", 1, Provider: "openai", RequestedModel: "gpt-5.4", ResolvedModel: "gpt-5.4",
                100, 50, null, null, 1.5m, CostConfidence: CostConfidence.Catalog,
                OccurredAtUtc: DateTimeOffset.UtcNow.AddDays(-2), RequestId: Guid.NewGuid().ToString("N")),
            cancellationToken: Ct);

        var service = new ManagementReportingService(rollupStore: rollup, null);
        var result = service.GetUsageSummary("week");

        Assert.True(result.Success);
        Assert.Equal(1, actual: result.Value!.Requests);
        Assert.Equal(1.5m, actual: result.Value!.CostUsd);
    }

    [Fact]
    public void GetUsageRollup_NoRollupStore_IsUnavailable()
    {
        var service = new ManagementReportingService(null, null);
        var result = service.GetUsageRollup(from: DateTimeOffset.UtcNow.AddDays(-1), to: DateTimeOffset.UtcNow,
            width: "day", groupBy: "model");

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.Unavailable, actual: result.ErrorType);
    }

    [Fact]
    public void GetUsageRollup_ToBeforeFrom_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var service = new ManagementReportingService(rollupStore: temp.CreateRollupStore(), null);

        var result = service.GetUsageRollup(from: DateTimeOffset.UtcNow, to: DateTimeOffset.UtcNow.AddDays(-1),
            width: "day", groupBy: "model");

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.InvalidRequest, actual: result.ErrorType);
    }

    [Theory]
    [InlineData("minute")]
    [InlineData("")]
    public void GetUsageRollup_UnknownWidth_IsInvalidRequest(string width)
    {
        using var temp = new TempDatabase();
        var service = new ManagementReportingService(rollupStore: temp.CreateRollupStore(), null);

        var result = service.GetUsageRollup(from: DateTimeOffset.UtcNow.AddDays(-1), to: DateTimeOffset.UtcNow,
            width: width, groupBy: "model");

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.InvalidRequest, actual: result.ErrorType);
    }

    [Fact]
    public void GetUsageRollup_UnknownGroupBy_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var service = new ManagementReportingService(rollupStore: temp.CreateRollupStore(), null);

        var result = service.GetUsageRollup(from: DateTimeOffset.UtcNow.AddDays(-1), to: DateTimeOffset.UtcNow,
            width: "day", groupBy: "region");

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.InvalidRequest, actual: result.ErrorType);
    }

    /// <summary>Serves a fixed set of comparison rows, filtering only on the lower bound as the real store does.</summary>
    private sealed class StubComparisonStore(IReadOnlyList<TaxonomyComparisonRecord> rows)
        : ITaxonomyComparisonStore
    {
        public Task<IReadOnlyList<long>> LoadPendingComparisonsAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        public Task UpsertAsync(TaxonomyComparisonRecord record, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TaxonomyComparisonRecord>> LoadSinceAsync(
            DateTimeOffset since, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TaxonomyComparisonRecord>>(
                [.. rows.Where(r => r.ComparedAtUtc >= since && (sessionId is null || r.SessionId == sessionId))]);
        }
    }
}