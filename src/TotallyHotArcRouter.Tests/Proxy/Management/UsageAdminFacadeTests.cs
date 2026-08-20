using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementFacade.GetUsageSummary"/>, <see cref="ManagementFacade.GetUsageRollup"/>,
/// and the budget-window fields <see cref="ManagementFacade.SetBudget"/> now accepts (Phase 4, §5.10/§5.15).
/// </summary>
public sealed class UsageAdminFacadeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelRoutingOptions SeedOptions() => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new ProviderOptions { BaseUrl = "https://api.openai.com", AuthHeaderName = "Authorization" }
        },
        ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }]
    };

    private static ManagementFacade CreateFacade(
        IUsageRollupStore? rollupStore = null,
        ProviderBudgetStore? budgetStore = null,
        IProviderConfigStore? store = null,
        TotallyHot.ArcRouter.Transcripts.ITaxonomyComparisonStore? comparisonStore = null) =>
        new(
            store ?? new InMemoryProviderConfigStore(SeedOptions()),
            Mock.Of<IEnvironmentVariableProvider>(),
            new HttpClient(),
            budgetStore,
            rollupStore: rollupStore,
            comparisonStore: comparisonStore);

    [Fact]
    public async Task GetRoutingRoiAsync_NoComparisonStore_IsUnavailableNotEmpty()
    {
        // "Unavailable" and "an empty range" are different answers: collapsing them would let a disabled
        // feature render as a measured break-even result.
        var result = await CreateFacade().GetRoutingRoiAsync(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, cancellationToken: Ct);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_InvertedRange_IsInvalidRequest()
    {
        var facade = CreateFacade(comparisonStore: new StubComparisonStore([]));
        var now = DateTimeOffset.UtcNow;

        var result = await facade.GetRoutingRoiAsync(now, now.AddDays(-1), cancellationToken: Ct);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public async Task GetRoutingRoiAsync_ProjectsTheCostHalfAndExcludesRowsPastTheUpperBound()
    {
        var now = DateTimeOffset.UtcNow;
        var inRange = MakeComparison(1, now.AddHours(-2), 0.09m);
        var pastEnd = MakeComparison(2, now.AddHours(2), 0.50m);
        var facade = CreateFacade(comparisonStore: new StubComparisonStore([inRange, pastEnd]));

        var result = await facade.GetRoutingRoiAsync(now.AddDays(-1), now, cancellationToken: Ct);

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
        var result = CreateFacade().GetUsageSummary("day");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void GetUsageSummary_UnknownWindow_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(rollupStore: temp.CreateRollupStore());

        var result = facade.GetUsageSummary("fortnight");

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

        var facade = CreateFacade(rollupStore: rollup);
        var result = facade.GetUsageSummary("week");

        Assert.True(result.Success);
        Assert.Equal(1, result.Value!.Requests);
        Assert.Equal(1.5m, result.Value!.CostUsd);
    }

    [Fact]
    public void GetUsageRollup_NoRollupStore_IsUnavailable()
    {
        var result = CreateFacade().GetUsageRollup(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "day", "model");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void GetUsageRollup_ToBeforeFrom_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(rollupStore: temp.CreateRollupStore());

        var result = facade.GetUsageRollup(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "day", "model");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Theory]
    [InlineData("minute")]
    [InlineData("")]
    public void GetUsageRollup_UnknownWidth_IsInvalidRequest(string width)
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(rollupStore: temp.CreateRollupStore());

        var result = facade.GetUsageRollup(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, width, "model");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public void GetUsageRollup_UnknownGroupBy_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(rollupStore: temp.CreateRollupStore());

        var result = facade.GetUsageRollup(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "day", "region");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public void SetBudget_WithRollingHoursWindow_PersistsWindowKindAndHours()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var budgetStore = temp.CreateBudgetStore(repository);
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(budgetStore: budgetStore, store: store);

        var result = facade.SetBudget("openai", new ProviderBudgetWriteRequest(100m, null, "RollingHours", 5));

        Assert.True(result.Success);
        var provider = Assert.Single(result.Value!.Providers);
        Assert.Equal("RollingHours", provider.WindowKind);
        Assert.NotNull(provider.NextResetUtc);
    }

    [Fact]
    public void SetBudget_RollingHoursWithoutHours_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var budgetStore = temp.CreateBudgetStore(repository);
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(budgetStore: budgetStore, store: store);

        var result = facade.SetBudget("openai", new ProviderBudgetWriteRequest(100m, null, "RollingHours", null));

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public void SetBudget_NoWindowSpecified_DefaultsToMonthly()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var budgetStore = temp.CreateBudgetStore(repository);
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(budgetStore: budgetStore, store: store);

        var result = facade.SetBudget("openai", new ProviderBudgetWriteRequest(100m, null));

        Assert.True(result.Success);
        Assert.Equal("Monthly", Assert.Single(result.Value!.Providers).WindowKind);
    }
}
