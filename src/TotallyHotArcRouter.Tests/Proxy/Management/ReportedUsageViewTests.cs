using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementFacade"/>'s <see cref="ProviderView.ReportedUsage"/> projection
/// (docs/router/secrets-at-rest-plan.md §8.1).
/// </summary>
public sealed class ReportedUsageViewTests
{
    private static ModelRoutingOptions SeedOptions()
    {
        return new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic"] = new() { BaseUrl = "https://api.anthropic.com", AuthHeaderName = "x-api-key" }
            }
        };
    }

    [Fact]
    public void ListProviders_NoPriceCatalogRepository_ReportedUsageIsNull()
    {
        var facade = new ManagementFacade(
            store: new InMemoryProviderConfigStore(SeedOptions()), environment: Mock.Of<IEnvironmentVariableProvider>(),
            httpClient: new HttpClient());

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.Null(provider.ReportedUsage);
    }

    [Fact]
    public void ListProviders_NothingFetchedYet_ReportedUsageIsNull()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        var facade = new ManagementFacade(
            store: new InMemoryProviderConfigStore(SeedOptions()), environment: Mock.Of<IEnvironmentVariableProvider>(),
            httpClient: new HttpClient(),
            dependencies: new ManagementFacadeDependencies { ReportedUsageRepository = repository });

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.Null(provider.ReportedUsage);
    }

    [Fact]
    public void ListProviders_UsageFetched_ProjectsRowsAndLatestFetchedAt()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new ReportedUsageRepository(temp.Database);
        var fetchedAt = new DateTimeOffset(2026, 1, 16, 4, 0, 0, offset: TimeSpan.Zero);
        repository.UpsertReportedUsage(
            providerKey: "anthropic",
            rows: [new ReportedUsageRow(UsageDay: new DateOnly(2026, 1, 15), Model: "claude-opus-4-1", 100, 50, 5, 10)],
            fetchedAtUtc: fetchedAt);
        var facade = new ManagementFacade(
            store: new InMemoryProviderConfigStore(SeedOptions()), environment: Mock.Of<IEnvironmentVariableProvider>(),
            httpClient: new HttpClient(),
            dependencies: new ManagementFacadeDependencies { ReportedUsageRepository = repository });

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.NotNull(provider.ReportedUsage);
        Assert.Equal(expected: fetchedAt, actual: provider.ReportedUsage!.FetchedAtUtc);
        var row = Assert.Single(provider.ReportedUsage.Rows);
        Assert.Equal(expected: "claude-opus-4-1", actual: row.Model);
        Assert.Equal(100, actual: row.InputTokens);
    }
}