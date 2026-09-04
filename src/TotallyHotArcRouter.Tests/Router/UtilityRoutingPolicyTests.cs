using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="UtilityRoutingPolicy"/> against the verification plan in
/// <c>docs/router/utility-model-routing.md</c> §B3/"Verification / test plan" - a stubbed
/// <see cref="IModelPriceCatalog"/> and an in-memory <see cref="RouterMemory"/>, no SQLite or network.
/// </summary>
public class UtilityRoutingPolicyTests
{
    private const string Dimension = "live:utility";

    [Fact]
    public async Task SelectModelAsync_ColdStart_PicksCheapestCatalogPricedCandidate()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice(modelName: "cheap", provider: "openai", 1m, 1m);
        catalog.SetPrice(modelName: "expensive", provider: "openai", 5m, 5m);
        var policy = Build(catalog: catalog, memory: new RouterMemory());

        var selected = await policy.SelectModelAsync(context: Context("cheap", "expensive"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "cheap", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_MemoryPopulated_PicksArgmaxOfRewardFormula()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice(modelName: "cheap-mediocre", provider: "openai", 1m, 1m);
        catalog.SetPrice(modelName: "pricier-better", provider: "openai", 5m, 5m);
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Dimension, model: "cheap-mediocre", 0.4);
        await memory.AddScoreAsync(dimension: Dimension, model: "pricier-better", 0.9);
        var policy = Build(catalog: catalog, memory: memory);

        // reward(cheap-mediocre) = 1*0.4 + (-0.1)*1  = 0.3
        // reward(pricier-better) = 1*0.9 + (-0.1)*5  = 0.4
        var selected = await policy.SelectModelAsync(context: Context("cheap-mediocre", "pricier-better"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "pricier-better", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_CandidateBelowQualityFloor_ExcludedEvenWhenCheapest()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice(modelName: "cheap-but-bad", provider: "openai", 1m, 1m);
        catalog.SetPrice(modelName: "pricier-ok", provider: "openai", 10m, 10m);
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Dimension, model: "cheap-but-bad", 0.05); // below the 0.3 default floor
        await memory.AddScoreAsync(dimension: Dimension, model: "pricier-ok", 0.5);
        var policy = Build(catalog: catalog, memory: memory);

        var selected = await policy.SelectModelAsync(context: Context("cheap-but-bad", "pricier-ok"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "pricier-ok", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_CandidateWithNoPrice_ExcludedFromCostRanking()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice(modelName: "priced", provider: "openai", 20m, 20m); // deliberately the pricier option
        var policy = Build(catalog: catalog, memory: new RouterMemory());

        var selected = await policy.SelectModelAsync(context: Context("priced", "unpriced"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "priced", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_CandidateWithPriceOlderThan24Hours_ExcludedFromCostRanking()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice(modelName: "priced", provider: "openai", 20m, 20m);
        catalog.SetPrice(modelName: "stale", provider: "openai", 1m, 1m, age: TimeSpan.FromHours(25));
        var policy = Build(catalog: catalog, memory: new RouterMemory());

        var selected = await policy.SelectModelAsync(context: Context("priced", "stale"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "priced", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_FreeProvider_IsCostRankedAtZero_ExemptFromFreshnessGate()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice(modelName: "paid", provider: "openai", 100m, 100m);
        var context = new RoutingContext(
            Dimension: Dimension,
            true,
            Candidates:
            [
                new RoutingCandidate(ModelName: "paid", Provider: "openai", false),
                new RoutingCandidate(ModelName: "free", Provider: "lmstudio", true)
            ]);
        var policy = Build(catalog: catalog, memory: new RouterMemory());

        var selected =
            await policy.SelectModelAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "free", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_UnobservedCandidate_IsNotGateDropped()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice(modelName: "observed-below-floor", provider: "openai", 1m, 1m);
        catalog.SetPrice(modelName: "unobserved", provider: "openai", 1m, 1m);
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Dimension, model: "observed-below-floor", 0.05); // gate-dropped
        // "unobserved" never scored - must survive the gate (s == null is not "bad").
        var policy = Build(catalog: catalog, memory: memory);

        var selected = await policy.SelectModelAsync(context: Context("observed-below-floor", "unobserved"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "unobserved", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_NoPricingAvailable_UnobservedCandidateDefaultsToZeroQuality()
    {
        // No candidate is priced, so selection falls to the unpriced-fallback tie-break (degradation
        // case 1, since both candidates pass the lowered gate). The floor is lowered so a
        // negatively-scored observed candidate still clears the gate, isolating the ?? default: an
        // unobserved candidate must rank as quality 0 - the same default the reward formula uses - not
        // as worse than an observed negative score.
        var options = Options.Create(new RoutingOptions { UtilityMinQualityScore = -1 });
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Dimension, model: "bad-observed", -0.5);
        // "unobserved" never scored - defaults to quality 0 per the type-level reward semantics.
        var policy = new UtilityRoutingPolicy(priceCatalog: new StubPriceCatalog(), memory: memory, options: options,
            logger: NullLogger<UtilityRoutingPolicy>.Instance);

        var selected = await policy.SelectModelAsync(context: Context("bad-observed", "unobserved"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "unobserved", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_PricedCandidateFailsGate_PrefersUnpricedGatePassingCandidate()
    {
        // "cheap-but-bad" is priced and would win degradation case 2's cheapest-priced fallback, but it
        // fails the quality gate. "unobserved" is unpriced but passes the gate (never scored). Per
        // §B3.4 the gate exists to keep a known-bad model out of rotation, so the gate-passing but
        // unpriced candidate must win over the cheaper gate-failing one.
        var catalog = new StubPriceCatalog();
        catalog.SetPrice(modelName: "cheap-but-bad", provider: "openai", 1m, 1m);
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: Dimension, model: "cheap-but-bad", 0.05); // below the 0.3 default floor
        // "unobserved" never scored and never priced - still preferred over a gate-failing priced model.
        var policy = Build(catalog: catalog, memory: memory);

        var selected = await policy.SelectModelAsync(context: Context("cheap-but-bad", "unobserved"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "unobserved", actual: selected);
    }

    [Fact]
    public async Task SelectModelAsync_CancelledToken_ThrowsBeforeSelecting()
    {
        var policy = Build(catalog: new StubPriceCatalog(), memory: new RouterMemory());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.SelectModelAsync(context: Context("only"), cancellationToken: cts.Token));
    }

    private static UtilityRoutingPolicy Build(IModelPriceCatalog catalog, RouterMemory memory)
    {
        return new UtilityRoutingPolicy(priceCatalog: catalog, memory: memory,
            options: Options.Create(new RoutingOptions()),
            logger: NullLogger<UtilityRoutingPolicy>.Instance);
    }

    private static RoutingContext Context(params string[] modelNames)
    {
        return new RoutingContext(
            Dimension: Dimension,
            true,
            Candidates: [.. modelNames.Select(name => new RoutingCandidate(ModelName: name, Provider: "openai", false))]);
    }

    /// <summary>
    /// An in-memory <see cref="IModelPriceCatalog"/> stub with a configurable per-key age, so tests can assert the
    /// 24h freshness floor directly.
    /// </summary>
    private sealed class StubPriceCatalog : IModelPriceCatalog
    {
        private readonly Dictionary<ModelKey, (ModelPrice Price, TimeSpan Age)> _prices = [];

        public ModelPrice? GetBestPriceForModel(ModelKey key, PriceContext context)
        {
            return _prices.TryGetValue(key: key, value: out var entry) ? entry.Price : null;
        }

        public ModelPrice? GetFreshPriceForRouting(ModelKey key, PriceContext context, TimeSpan maxAge)
        {
            return _prices.TryGetValue(key: key, value: out var entry) && entry.Age <= maxAge ? entry.Price : null;
        }

        public void Invalidate()
        {
        }

        public void SetPrice(string modelName, string provider, decimal input, decimal output, TimeSpan? age = null)
        {
            _prices[new ModelKey(ModelName: modelName, Provider: provider)] = (
                new ModelPrice(InputPerMillionTokens: input, OutputPerMillionTokens: output), age ?? TimeSpan.Zero);
        }
    }
}