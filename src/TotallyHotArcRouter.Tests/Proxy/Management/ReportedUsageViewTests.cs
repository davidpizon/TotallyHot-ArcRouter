using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using TotallyHot.ArcRouter.Tests.Proxy;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>Covers <see cref="ManagementFacade"/>'s <see cref="ProviderView.ReportedUsage"/> projection (docs/router/secrets-at-rest-plan.md §8.1).</summary>
public sealed class ReportedUsageViewTests
{
    private static ModelRoutingOptions SeedOptions() => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new ProviderOptions { BaseUrl = "https://api.anthropic.com", AuthHeaderName = "x-api-key" }
        }
    };

    [Fact]
    public void ListProviders_NoPriceCatalogRepository_ReportedUsageIsNull()
    {
        var facade = new ManagementFacade(
            new InMemoryProviderConfigStore(SeedOptions()), Mock.Of<IEnvironmentVariableProvider>(), new HttpClient());

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.Null(provider.ReportedUsage);
    }

    [Fact]
    public void ListProviders_NothingFetchedYet_ReportedUsageIsNull()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceCatalogRepository(temp.Database);
        var facade = new ManagementFacade(
            new InMemoryProviderConfigStore(SeedOptions()), Mock.Of<IEnvironmentVariableProvider>(), new HttpClient(),
            new ManagementFacadeDependencies { PriceCatalogRepository = repository });

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.Null(provider.ReportedUsage);
    }

    [Fact]
    public void ListProviders_UsageFetched_ProjectsRowsAndLatestFetchedAt()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceCatalogRepository(temp.Database);
        var fetchedAt = new DateTimeOffset(2026, 1, 16, 4, 0, 0, TimeSpan.Zero);
        repository.UpsertReportedUsage(
            "anthropic",
            [new ReportedUsageRow(new DateOnly(2026, 1, 15), "claude-opus-4-1", 100, 50, 5, 10)],
            fetchedAt);
        var facade = new ManagementFacade(
            new InMemoryProviderConfigStore(SeedOptions()), Mock.Of<IEnvironmentVariableProvider>(), new HttpClient(),
            new ManagementFacadeDependencies { PriceCatalogRepository = repository });

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.NotNull(provider.ReportedUsage);
        Assert.Equal(fetchedAt, provider.ReportedUsage!.FetchedAtUtc);
        var row = Assert.Single(provider.ReportedUsage.Rows);
        Assert.Equal("claude-opus-4-1", row.Model);
        Assert.Equal(100, row.InputTokens);
    }
}
