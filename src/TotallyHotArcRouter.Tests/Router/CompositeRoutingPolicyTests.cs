using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="AgentRouterPolicy"/> and <see cref="CompositeRoutingPolicy"/>'s dispatch between the
/// utility and general policies (<c>docs/router/utility-model-routing.md</c> §B3).
/// </summary>
public class CompositeRoutingPolicyTests
{
    [Fact]
    public async Task AgentRouterPolicy_DelegatesToAgentAsARouterSelection()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync("live:code_generation", "model-a", 0.4);
        await memory.AddScoreAsync("live:code_generation", "model-b", 0.9);
        var router = new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            memory);
        var policy = new AgentRouterPolicy(router);
        var context = new RoutingContext(
            "live:code_generation",
            IsUtility: false,
            Candidates: [new RoutingCandidate("model-a", "openai", IsFree: false), new RoutingCandidate("model-b", "openai", IsFree: false)]);

        var selected = await policy.SelectModelAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("model-b", selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_UtilityContext_DispatchesToUtilityPolicy()
    {
        var catalog = new PassthroughPriceCatalog();
        catalog.SetPrice("cheap", "openai", 1m, 1m);
        catalog.SetPrice("pricey", "openai", 50m, 50m);
        var utilityPolicy = new UtilityRoutingPolicy(catalog, new RouterMemory(), Options.Create(new RoutingOptions()), NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0 }),
            new RouterMemory()));
        var composite = new CompositeRoutingPolicy(utilityPolicy, generalPolicy);
        var context = new RoutingContext(
            "live:utility",
            IsUtility: true,
            Candidates: [new RoutingCandidate("cheap", "openai", IsFree: false), new RoutingCandidate("pricey", "openai", IsFree: false)]);

        var selected = await composite.SelectModelAsync(context, TestContext.Current.CancellationToken);

        // Cold-start utility selection ranks by price alone - cheapest wins.
        Assert.Equal("cheap", selected);
    }

    [Fact]
    public async Task CompositeRoutingPolicy_NonUtilityContext_DispatchesToGeneralPolicy()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync("live:code_generation", "model-a", 0.9);
        var utilityPolicy = new UtilityRoutingPolicy(new PassthroughPriceCatalog(), new RouterMemory(), Options.Create(new RoutingOptions()), NullLogger<UtilityRoutingPolicy>.Instance);
        var generalPolicy = new AgentRouterPolicy(new AgentAsARouter(
            NullLogger<AgentAsARouter>.Instance,
            Options.Create(new RoutingOptions { EnableExploration = false, ExplorationRate = 0, DefaultModel = "model-a" }),
            memory));
        var composite = new CompositeRoutingPolicy(utilityPolicy, generalPolicy);
        var context = new RoutingContext(
            "live:code_generation",
            IsUtility: false,
            Candidates: [new RoutingCandidate("model-a", "openai", IsFree: false)]);

        var selected = await composite.SelectModelAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("model-a", selected);
    }

    private sealed class PassthroughPriceCatalog : IModelPriceCatalog
    {
        private readonly Dictionary<ModelKey, ModelPrice> _prices = [];

        public void SetPrice(string modelName, string provider, decimal input, decimal output) =>
            _prices[new ModelKey(modelName, provider)] = new ModelPrice(input, output);

        public ModelPrice? GetBestPriceForModel(ModelKey key, PriceContext context) =>
            _prices.TryGetValue(key, out var price) ? price : null;

        public ModelPrice? GetFreshPriceForRouting(ModelKey key, PriceContext context, TimeSpan maxAge) =>
            _prices.TryGetValue(key, out var price) ? price : null;

        public void Invalidate()
        {
        }
    }
}
