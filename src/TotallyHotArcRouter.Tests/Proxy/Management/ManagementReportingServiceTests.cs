using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementReportingService.GetUsageSummary"/>, <see cref="ManagementReportingService.GetUsageRollup"/>,
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
        var service = new ManagementReportingService(rollupStore: null, comparisonStore: null);
        var result = await service.GetRoutingRoiAsync(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, cancellationToken: Ct);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_InvertedRange_IsInvalidRequest()
    {
        var service = new ManagementReportingService(rollupStore: null, new StubComparisonStore([]));
        var now = DateTimeOffset.UtcNow;

        var result = await service.GetRoutingRoiAsync(now, now.AddDays(-1), cancellationToken: Ct);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_ProjectsTheCostHalfAndExcludesRowsPastTheUpperBound()
    {
        var now = DateTimeOffset.UtcNow;
        var inRange = MakeComparison(1, now.AddHours(-2), 0.09m);
        var pastEnd = MakeComparison(2, now.AddHours(2), 0.50m);
        var service = new ManagementReportingService(rollupStore: null, new StubComparisonStore([inRange, pastEnd]));

        var result = await service.GetRoutingRoiAsync(now.AddDays(-1), now, cancellationToken: Ct);

        Assert.True(result.Success);
        var point = Assert.Single(result.Value!);
        Assert.Equal(0.09m, point.EstimatedNetSavingsUsd);
        Assert.Equal("kimi-k2.5", point.RoutedModel);
        Assert.Equal("glm-5", point.BaselineModel);
    }

    /// <summary>Builds a comparison row carrying a known savings figure at a known instant.</summary>
    private static TotallyHot.ArcRouter.Transcripts.TaxonomyComparisonRecord MakeComparison(
        long id, DateTimeOffset comparedAt, decimal savings) =>
        new(
            TranscriptId: id,
            ComparedAtUtc: comparedAt,
            SessionId: "session-1",
            ObservedScore: 0.8,
            DimensionPredictedScore: 0.7,
            ClusterPredictedScore: 0.79,
            DimensionAbsoluteError: 0.1,
            ClusterAbsoluteError: 0.01,
            IsClustered: true,
            IsExploratory: false,
            RoutedModel: "kimi-k2.5",
            BaselineModel: "glm-5",
            ActualCostUsd: 0.02m,
            BaselineEstimatedCostUsd: 0.02m + savings,
            EstimatedNetSavingsUsd: savings,
            BaselinePredictedScore: 0.75,
            EstimatedRegret: -0.05);

    /// <summary>Serves a fixed set of comparison rows, filtering only on the lower bound as the real store does.</summary>
    private sealed class StubComparisonStore(IReadOnlyList<TotallyHot.ArcRouter.Transcripts.TaxonomyComparisonRecord> rows)
        : TotallyHot.ArcRouter.Transcripts.ITaxonomyComparisonStore
    {
        public Task<IReadOnlyList<long>> LoadPendingComparisonsAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<long>>([]);

        public Task UpsertAsync(TotallyHot.ArcRouter.Transcripts.TaxonomyComparisonRecord record, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<TotallyHot.ArcRouter.Transcripts.TaxonomyComparisonRecord>> LoadSinceAsync(
            DateTimeOffset since, string? sessionId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TotallyHot.ArcRouter.Transcripts.TaxonomyComparisonRecord>>(
                [.. rows.Where(r => r.ComparedAtUtc >= since && (sessionId is null || r.SessionId == sessionId))]);
    }

    [Fact]
    public void GetUsageSummary_NoRollupStore_IsUnavailable()
    {
        var service = new ManagementReportingService(rollupStore: null, comparisonStore: null);
        var result = service.GetUsageSummary("day");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void GetUsageSummary_UnknownWindow_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var service = new ManagementReportingService(temp.CreateRollupStore(), comparisonStore: null);

        var result = service.GetUsageSummary("fortnight");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public async Task GetUsageSummary_WithData_ReturnsTotals()
    {
        using var temp = new TempDatabase();
        var rollup = temp.CreateRollupStore();
        var ledger = temp.CreateUsageLedger(rollup);
        await ledger.RecordAsync(
            new UsageLedgerEntry(
                "sess-1", 1, "openai", "gpt-5.4", "gpt-5.4",
                100, 50, null, null, 1.5m, CostConfidence.Catalog,
                DateTimeOffset.UtcNow.AddDays(-2), Guid.NewGuid().ToString("N")),
            Ct);

        var service = new ManagementReportingService(rollup, comparisonStore: null);
        var result = service.GetUsageSummary("week");

        Assert.True(result.Success);
        Assert.Equal(1, result.Value!.Requests);
        Assert.Equal(1.5m, result.Value!.CostUsd);
    }

    [Fact]
    public void GetUsageRollup_NoRollupStore_IsUnavailable()
    {
        var service = new ManagementReportingService(rollupStore: null, comparisonStore: null);
        var result = service.GetUsageRollup(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "day", "model");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void GetUsageRollup_ToBeforeFrom_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var service = new ManagementReportingService(temp.CreateRollupStore(), comparisonStore: null);

        var result = service.GetUsageRollup(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "day", "model");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Theory]
    [InlineData("minute")]
    [InlineData("")]
    public void GetUsageRollup_UnknownWidth_IsInvalidRequest(string width)
    {
        using var temp = new TempDatabase();
        var service = new ManagementReportingService(temp.CreateRollupStore(), comparisonStore: null);

        var result = service.GetUsageRollup(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, width, "model");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public void GetUsageRollup_UnknownGroupBy_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var service = new ManagementReportingService(temp.CreateRollupStore(), comparisonStore: null);

        var result = service.GetUsageRollup(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "day", "region");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }
}
