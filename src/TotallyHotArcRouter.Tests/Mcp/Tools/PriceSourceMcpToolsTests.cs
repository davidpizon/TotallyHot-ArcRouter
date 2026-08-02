using TotallyHot.ArcRouter.Mcp.Tools;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Mcp.Tools;

/// <summary>Covers <see cref="PriceSourceMcpTools"/>: delegation to the price-source/catalog stores.</summary>
public sealed class PriceSourceMcpToolsTests
{
    [Fact]
    public void ListPriceSources_ReturnsStoreState()
    {
        using var temp = new TempDatabase();
        temp.SeedExtraSource("openrouter", enabled: true, priorityScore: 5);
        var toggleStore = temp.CreateToggleStore();
        var tools = CreateTools(temp, toggleStore);

        var sources = tools.ListPriceSources();

        Assert.Contains(sources, s => s.Name == "openrouter");
    }

    [Fact]
    public void SetPriceSourceEnabled_KnownSource_ReturnsSuccess()
    {
        using var temp = new TempDatabase();
        var toggleStore = temp.CreateToggleStore();
        var tools = CreateTools(temp, toggleStore);

        var result = tools.SetPriceSourceEnabled("litellm", false);

        Assert.False(toggleStore.IsEnabled("litellm"));
        var successProperty = result.GetType().GetProperty("success");
        Assert.NotNull(successProperty);
        Assert.Equal(true, successProperty!.GetValue(result));
    }

    [Fact]
    public void SetPriceSourceEnabled_UnknownSource_ReturnsError()
    {
        using var temp = new TempDatabase();
        var tools = CreateTools(temp, temp.CreateToggleStore());

        var result = tools.SetPriceSourceEnabled("does-not-exist", true);

        var errorProperty = result.GetType().GetProperty("error");
        Assert.NotNull(errorProperty);
        Assert.NotNull(errorProperty!.GetValue(result));
    }

    [Fact]
    public void ReorderPriceSources_InvalidNameSet_ReturnsError()
    {
        using var temp = new TempDatabase();
        var tools = CreateTools(temp, temp.CreateToggleStore());

        var result = tools.ReorderPriceSources(["not-a-real-source"]);

        var errorProperty = result.GetType().GetProperty("error");
        Assert.NotNull(errorProperty);
        Assert.NotNull(errorProperty!.GetValue(result));
    }

    [Fact]
    public async Task RefreshPriceSourcesAsync_NoEnabledSources_ReturnsSummaryWithNoOutcomes()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var toggleStore = temp.CreateToggleStore(repository);
        var registry = new Mock<IPriceSourceRegistry>();
        registry.Setup(r => r.EnabledClients).Returns([]);
        var ingestionService = new PriceCatalogIngestionService(registry.Object, repository, toggleStore, NullLogger<PriceCatalogIngestionService>.Instance);
        var tools = new PriceSourceMcpTools(toggleStore, ingestionService, Mock.Of<IModelPriceLookup>());

        var summary = await tools.RefreshPriceSourcesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(summary.Outcomes);
    }

    [Fact]
    public void GetModelPrice_DelegatesToPriceLookupWithMatchingKey()
    {
        using var temp = new TempDatabase();
        var toggleStore = temp.CreateToggleStore();
        var expected = new ModelPrice(1.5m, 2.5m);
        var priceLookup = new Mock<IModelPriceLookup>();
        priceLookup.Setup(p => p.TryGetPrice(new ModelKey("gpt-5.4", "openai"))).Returns(expected);
        var tools = CreateTools(temp, toggleStore, priceLookup.Object);

        var price = tools.GetModelPrice("gpt-5.4", "openai");

        Assert.Equal(expected, price);
    }

    private static PriceSourceMcpTools CreateTools(TempDatabase temp, PriceSourceToggleStore toggleStore, IModelPriceLookup? priceLookup = null)
    {
        var registry = Mock.Of<IPriceSourceRegistry>();
        var ingestionService = new PriceCatalogIngestionService(registry, temp.CreateRepository(), toggleStore, NullLogger<PriceCatalogIngestionService>.Instance);
        return new PriceSourceMcpTools(toggleStore, ingestionService, priceLookup ?? Mock.Of<IModelPriceLookup>());
    }
}

