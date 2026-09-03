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
        catalog.SetPrice("cheap", "openai", input: 1m, output: 1m);
        catalog.SetPrice("expensive", "openai", input: 5m, output: 5m);
        var policy = Build(catalog, new RouterMemory());

        var selected = await policy.SelectModelAsync(Context("cheap", "expensive"), TestContext.Current.CancellationToken);

        Assert.Equal("cheap", selected);
    }

    [Fact]
    public async Task SelectModelAsync_MemoryPopulated_PicksArgmaxOfRewardFormula()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice("cheap-mediocre", "openai", input: 1m, output: 1m);
        catalog.SetPrice("pricier-better", "openai", input: 5m, output: 5m);
        var memory = new RouterMemory();
        await memory.AddScoreAsync(Dimension, "cheap-mediocre", 0.4);
        await memory.AddScoreAsync(Dimension, "pricier-better", 0.9);
        var policy = Build(catalog, memory);

        // reward(cheap-mediocre) = 1*0.4 + (-0.1)*1  = 0.3
        // reward(pricier-better) = 1*0.9 + (-0.1)*5  = 0.4
        var selected = await policy.SelectModelAsync(Context("cheap-mediocre", "pricier-better"), TestContext.Current.CancellationToken);

        Assert.Equal("pricier-better", selected);
    }

    [Fact]
    public async Task SelectModelAsync_CandidateBelowQualityFloor_ExcludedEvenWhenCheapest()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice("cheap-but-bad", "openai", input: 1m, output: 1m);
        catalog.SetPrice("pricier-ok", "openai", input: 10m, output: 10m);
        var memory = new RouterMemory();
        await memory.AddScoreAsync(Dimension, "cheap-but-bad", 0.05); // below the 0.3 default floor
        await memory.AddScoreAsync(Dimension, "pricier-ok", 0.5);
        var policy = Build(catalog, memory);

        var selected = await policy.SelectModelAsync(Context("cheap-but-bad", "pricier-ok"), TestContext.Current.CancellationToken);

        Assert.Equal("pricier-ok", selected);
    }

    [Fact]
    public async Task SelectModelAsync_CandidateWithNoPrice_ExcludedFromCostRanking()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice("priced", "openai", input: 20m, output: 20m); // deliberately the pricier option
        var policy = Build(catalog, new RouterMemory());

        var selected = await policy.SelectModelAsync(Context("priced", "unpriced"), TestContext.Current.CancellationToken);

        Assert.Equal("priced", selected);
    }

    [Fact]
    public async Task SelectModelAsync_CandidateWithPriceOlderThan24Hours_ExcludedFromCostRanking()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice("priced", "openai", input: 20m, output: 20m);
        catalog.SetPrice("stale", "openai", input: 1m, output: 1m, age: TimeSpan.FromHours(25));
        var policy = Build(catalog, new RouterMemory());

        var selected = await policy.SelectModelAsync(Context("priced", "stale"), TestContext.Current.CancellationToken);

        Assert.Equal("priced", selected);
    }

    [Fact]
    public async Task SelectModelAsync_FreeProvider_IsCostRankedAtZero_ExemptFromFreshnessGate()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice("paid", "openai", input: 100m, output: 100m);
        var context = new RoutingContext(
            Dimension,
            IsUtility: true,
            Candidates:
            [
                new RoutingCandidate("paid", "openai", IsFree: false),
                new RoutingCandidate("free", "lmstudio", IsFree: true),
            ]);
        var policy = Build(catalog, new RouterMemory());

        var selected = await policy.SelectModelAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("free", selected);
    }

    [Fact]
    public async Task SelectModelAsync_UnobservedCandidate_IsNotGateDropped()
    {
        var catalog = new StubPriceCatalog();
        catalog.SetPrice("observed-below-floor", "openai", input: 1m, output: 1m);
        catalog.SetPrice("unobserved", "openai", input: 1m, output: 1m);
        var memory = new RouterMemory();
        await memory.AddScoreAsync(Dimension, "observed-below-floor", 0.05); // gate-dropped
        // "unobserved" never scored - must survive the gate (s == null is not "bad").
        var policy = Build(catalog, memory);

        var selected = await policy.SelectModelAsync(Context("observed-below-floor", "unobserved"), TestContext.Current.CancellationToken);

        Assert.Equal("unobserved", selected);
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
        await memory.AddScoreAsync(Dimension, "bad-observed", -0.5);
        // "unobserved" never scored - defaults to quality 0 per the type-level reward semantics.
        var policy = new UtilityRoutingPolicy(new StubPriceCatalog(), memory, options, NullLogger<UtilityRoutingPolicy>.Instance);

        var selected = await policy.SelectModelAsync(Context("bad-observed", "unobserved"), TestContext.Current.CancellationToken);

        Assert.Equal("unobserved", selected);
    }

    [Fact]
    public async Task SelectModelAsync_PricedCandidateFailsGate_PrefersUnpricedGatePassingCandidate()
    {
        // "cheap-but-bad" is priced and would win degradation case 2's cheapest-priced fallback, but it
        // fails the quality gate. "unobserved" is unpriced but passes the gate (never scored). Per
        // §B3.4 the gate exists to keep a known-bad model out of rotation, so the gate-passing but
        // unpriced candidate must win over the cheaper gate-failing one.
        var catalog = new StubPriceCatalog();
        catalog.SetPrice("cheap-but-bad", "openai", input: 1m, output: 1m);
        var memory = new RouterMemory();
        await memory.AddScoreAsync(Dimension, "cheap-but-bad", 0.05); // below the 0.3 default floor
        // "unobserved" never scored and never priced - still preferred over a gate-failing priced model.
        var policy = Build(catalog, memory);

        var selected = await policy.SelectModelAsync(Context("cheap-but-bad", "unobserved"), TestContext.Current.CancellationToken);

        Assert.Equal("unobserved", selected);
    }

    [Fact]
    public async Task SelectModelAsync_CancelledToken_ThrowsBeforeSelecting()
    {
        var policy = Build(new StubPriceCatalog(), new RouterMemory());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => policy.SelectModelAsync(Context("only"), cts.Token));
    }

    private static UtilityRoutingPolicy Build(IModelPriceCatalog catalog, RouterMemory memory) =>
        new(catalog, memory, Options.Create(new RoutingOptions()), NullLogger<UtilityRoutingPolicy>.Instance);

    private static RoutingContext Context(params string[] modelNames) =>
        new(
            Dimension,
            IsUtility: true,
            Candidates: modelNames.Select(name => new RoutingCandidate(name, "openai", IsFree: false)).ToList());

    /// <summary>An in-memory <see cref="IModelPriceCatalog"/> stub with a configurable per-key age, so tests can assert the 24h freshness floor directly.</summary>
    private sealed class StubPriceCatalog : IModelPriceCatalog
    {
        private readonly Dictionary<ModelKey, (ModelPrice Price, TimeSpan Age)> _prices = [];

        public void SetPrice(string modelName, string provider, decimal input, decimal output, TimeSpan? age = null) =>
            _prices[new ModelKey(modelName, provider)] = (new ModelPrice(input, output), age ?? TimeSpan.Zero);

        public ModelPrice? GetBestPriceForModel(ModelKey key, PriceContext context) =>
            _prices.TryGetValue(key, out var entry) ? entry.Price : null;

        public ModelPrice? GetFreshPriceForRouting(ModelKey key, PriceContext context, TimeSpan maxAge) =>
            _prices.TryGetValue(key, out var entry) && entry.Age <= maxAge ? entry.Price : null;

        public void Invalidate()
        {
        }
    }
}
