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
        IProviderConfigStore? store = null) =>
        new(
            store ?? new InMemoryProviderConfigStore(SeedOptions()),
            Mock.Of<IEnvironmentVariableProvider>(),
            new HttpClient(),
            budgetStore,
            rollupStore: rollupStore);

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
