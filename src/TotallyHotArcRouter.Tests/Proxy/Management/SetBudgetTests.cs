using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>Covers the budget-window fields <see cref="ManagementFacade.SetBudget"/> accepts (Phase 4, §5.10).</summary>
public sealed class SetBudgetTests
{
    private static ModelRoutingOptions SeedOptions()
    {
        return new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { BaseUrl = "https://api.openai.com", AuthHeaderName = "Authorization" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }
            ]
        };
    }

    private static ManagementFacade CreateFacade(ProviderBudgetStore? budgetStore = null,
        IProviderConfigStore? store = null)
    {
        return new ManagementFacade(
            store: store ?? new InMemoryProviderConfigStore(SeedOptions()),
            environment: Mock.Of<IEnvironmentVariableProvider>(),
            httpClient: new HttpClient(),
            dependencies: new ManagementFacadeDependencies
            {
                BudgetStore = budgetStore
            });
    }

    [Fact]
    public void SetBudget_WithRollingHoursWindow_PersistsWindowKindAndHours()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateBudgetRepository();
        var budgetStore = temp.CreateBudgetStore(repository);
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(budgetStore: budgetStore, store: store);

        var result = facade.SetBudget(providerKey: "openai",
            request: new ProviderBudgetWriteRequest(100m, null, WindowKind: "RollingHours", 5));

        Assert.True(result.Success);
        var provider = Assert.Single(result.Value!.Providers);
        Assert.Equal(expected: "RollingHours", actual: provider.WindowKind);
        Assert.NotNull(provider.NextResetUtc);
    }

    [Fact]
    public void SetBudget_RollingHoursWithoutHours_IsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateBudgetRepository();
        var budgetStore = temp.CreateBudgetStore(repository);
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(budgetStore: budgetStore, store: store);

        var result = facade.SetBudget(providerKey: "openai",
            request: new ProviderBudgetWriteRequest(100m, null, WindowKind: "RollingHours"));

        Assert.False(result.Success);
        Assert.Equal(expected: ManagementErrorType.InvalidRequest, actual: result.ErrorType);
    }

    [Fact]
    public void SetBudget_NoWindowSpecified_DefaultsToMonthly()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateBudgetRepository();
        var budgetStore = temp.CreateBudgetStore(repository);
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(budgetStore: budgetStore, store: store);

        var result = facade.SetBudget(providerKey: "openai", request: new ProviderBudgetWriteRequest(100m, null));

        Assert.True(result.Success);
        Assert.Equal(expected: "Monthly", actual: Assert.Single(result.Value!.Providers).WindowKind);
    }
}