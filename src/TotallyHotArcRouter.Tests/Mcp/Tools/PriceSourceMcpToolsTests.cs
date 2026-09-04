using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TotallyHot.ArcRouter.Mcp.Tools;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Mcp.Tools;

/// <summary>Covers <see cref="PriceSourceMcpTools"/>: delegation to the price-source/catalog stores.</summary>
public sealed class PriceSourceMcpToolsTests
{
    [Fact]
    public void ListPriceSources_ReturnsStoreState()
    {
        using var temp = new TempDatabase();
        temp.SeedExtraSource(name: "openrouter", true, 5);
        var toggleStore = temp.CreateToggleStore();
        var tools = CreateTools(temp: temp, toggleStore: toggleStore);

        var sources = tools.ListPriceSources();

        Assert.Contains(collection: sources, filter: s => s.Name == "openrouter");
    }

    [Fact]
    public void SetPriceSourceEnabled_KnownSource_ReturnsSuccess()
    {
        using var temp = new TempDatabase();
        var toggleStore = temp.CreateToggleStore();
        var tools = CreateTools(temp: temp, toggleStore: toggleStore);

        var result = tools.SetPriceSourceEnabled(sourceName: "litellm", false);

        Assert.False(toggleStore.IsEnabled("litellm"));
        var successProperty = result.GetType().GetProperty("success");
        Assert.NotNull(successProperty);
        Assert.Equal(true, actual: successProperty.GetValue(result));
    }

    [Fact]
    public void SetPriceSourceEnabled_UnknownSource_ReturnsError()
    {
        using var temp = new TempDatabase();
        var tools = CreateTools(temp: temp, toggleStore: temp.CreateToggleStore());

        var result = tools.SetPriceSourceEnabled(sourceName: "does-not-exist", true);

        var errorProperty = result.GetType().GetProperty("error");
        Assert.NotNull(errorProperty);
        Assert.NotNull(errorProperty.GetValue(result));
    }

    [Fact]
    public async Task ReorderPriceSourcesAsync_InvalidNameSet_ReturnsError()
    {
        using var temp = new TempDatabase();
        var tools = CreateTools(temp: temp, toggleStore: temp.CreateToggleStore());

        var result = await tools.ReorderPriceSourcesAsync(namesInPriorityOrder: ["not-a-real-source"],
            cancellationToken: TestContext.Current.CancellationToken);

        var errorProperty = result.GetType().GetProperty("error");
        Assert.NotNull(errorProperty);
        Assert.NotNull(errorProperty.GetValue(result));
    }

    [Fact]
    public async Task ReorderPriceSourcesAsync_ValidNameSet_RecomputesContestedCellImmediately()
    {
        // The whole point of wiring recompute into the MCP tool: an MCP-driven reorder must flip a
        // contested cell's winner using only stored observations, with no fetch in between - parity with
        // the Governance panel's drag-to-reorder.
        using var temp = new TempDatabase();
        temp.SeedExtraSource(name: "openrouter", true, 5);
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        var toggleStore = temp.CreateToggleStore(sourceRepository);

        // litellm (priority 0, seeded default) starts behind openrouter (priority 5).
        repository.UpsertPrices(sourceName: "litellm", 0,
            prices: [Price(model: "gpt-4o", provider: "openai", 2.00m, 8.00m)], asOfUtc: DateTimeOffset.UtcNow);
        repository.UpsertPrices(sourceName: "openrouter", 5,
            prices: [Price(model: "gpt-4o", provider: "openai", 3.00m, 12.00m)], asOfUtc: DateTimeOffset.UtcNow);

        Assert.Equal(3.00m,
            actual: repository.GetFreshPrice(key: new ModelKey(ModelName: "gpt-4o", Provider: "openai"),
                maxAge: TimeSpan.FromHours(24))!.InputPerMillionTokens);

        var registry = Mock.Of<IPriceSourceRegistry>();
        var ingestionService = new PriceCatalogIngestionService(registry: registry, repository: repository,
            sourceRepository: sourceRepository, toggleStore: toggleStore,
            logger: NullLogger<PriceCatalogIngestionService>.Instance);
        var tools = new PriceSourceMcpTools(toggleStore: toggleStore, ingestionService: ingestionService,
            priceLookup: Mock.Of<IModelPriceLookup>());

        var result = await tools.ReorderPriceSourcesAsync(namesInPriorityOrder: ["litellm", "openrouter"],
            cancellationToken: TestContext.Current.CancellationToken);

        var successProperty = result.GetType().GetProperty("success");
        Assert.NotNull(successProperty);
        Assert.Equal(true, actual: successProperty.GetValue(result));
        // litellm now outranks openrouter, and its own stored observation was never re-fetched - this
        // value can only have come from model_price_observations.
        Assert.Equal(2.00m,
            actual: repository.GetFreshPrice(key: new ModelKey(ModelName: "gpt-4o", Provider: "openai"),
                maxAge: TimeSpan.FromHours(24))!.InputPerMillionTokens);
    }

    private static NormalizedPrice Price(string model, string provider, decimal input, decimal output)
    {
        return new NormalizedPrice(ModelIdentifier: model, Provider: provider, StandardInputPrice: input,
            StandardOutputPrice: output,
            null, null, null);
    }

    [Fact]
    public async Task RefreshPriceSourcesAsync_NoEnabledSources_ReturnsSummaryWithNoOutcomes()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        var toggleStore = temp.CreateToggleStore(sourceRepository);
        var registry = new Mock<IPriceSourceRegistry>();
        registry.Setup(r => r.EnabledClients).Returns([]);
        var ingestionService = new PriceCatalogIngestionService(registry: registry.Object, repository: repository,
            sourceRepository: sourceRepository, toggleStore: toggleStore,
            logger: NullLogger<PriceCatalogIngestionService>.Instance);
        var tools = new PriceSourceMcpTools(toggleStore: toggleStore, ingestionService: ingestionService,
            priceLookup: Mock.Of<IModelPriceLookup>());

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
        var tools = CreateTools(temp: temp, toggleStore: toggleStore, priceLookup: priceLookup.Object);

        var price = tools.GetModelPrice(modelName: "gpt-5.4", provider: "openai");

        Assert.Equal(expected: expected, actual: price);
    }

    private static PriceSourceMcpTools CreateTools(TempDatabase temp, PriceSourceToggleStore toggleStore,
        IModelPriceLookup? priceLookup = null)
    {
        var registry = Mock.Of<IPriceSourceRegistry>();
        var ingestionService = new PriceCatalogIngestionService(registry: registry, repository: temp.CreateRepository(),
            sourceRepository: temp.CreateSourceRepository(), toggleStore: toggleStore,
            logger: NullLogger<PriceCatalogIngestionService>.Instance);
        return new PriceSourceMcpTools(toggleStore: toggleStore, ingestionService: ingestionService,
            priceLookup: priceLookup ?? Mock.Of<IModelPriceLookup>());
    }
}