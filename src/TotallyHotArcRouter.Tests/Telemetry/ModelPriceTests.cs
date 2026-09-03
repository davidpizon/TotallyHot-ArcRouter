using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="ModelPrice.EstimateCost"/>.</summary>
public class ModelPriceTests
{
    [Fact]
    public void EstimateCost_ComputesPerMillionTokenRatesCorrectly()
    {
        var price = new ModelPrice(3.00m, 15.00m);

        var cost = price.EstimateCost(1_000_000, 1_000_000);

        Assert.Equal(18.00m, actual: cost);
    }

    [Fact]
    public void EstimateCost_FractionOfAMillionTokens_ScalesLinearly()
    {
        var price = new ModelPrice(2.00m, 10.00m);

        // 500,000 prompt tokens = half of $2.00 = $1.00; 100,000 completion tokens = 1/10 of $10.00 = $1.00.
        var cost = price.EstimateCost(500_000, 100_000);

        Assert.Equal(2.00m, actual: cost);
    }

    [Fact]
    public void EstimateCost_ZeroTokens_IsZero()
    {
        var price = new ModelPrice(5.00m, 20.00m);

        var cost = price.EstimateCost(0, 0);

        Assert.Equal(0m, actual: cost);
    }

    [Fact]
    public void EstimateCost_UsageInfo_WithFullCacheRates_PricesEachComponentAtItsOwnRate()
    {
        var price = new ModelPrice(
            3.00m,
            15.00m,
            0.30m,
            3.75m);
        var usage = new UsageInfo(1_000_000, 1_000_000, 1_000_000, 1_000_000);

        var cost = price.EstimateCost(usage);

        Assert.Equal(expected: 3.00m + 15.00m + 0.30m + 3.75m, actual: cost);
    }

    [Fact]
    public void EstimateCost_UsageInfo_MissingCacheRates_FallsBackToStandardInputRate()
    {
        var price = new ModelPrice(3.00m, 15.00m);
        var usage = new UsageInfo(0, 0, 1_000_000, 1_000_000);

        var cost = price.EstimateCost(usage);

        // Both cache dimensions fall back to InputPerMillionTokens (3.00) when the catalog has no published
        // cache rate - the conservative overestimate documented on ModelPrice.EstimateCost(UsageInfo).
        Assert.Equal(6.00m, actual: cost);
    }

    [Fact]
    public void EstimateCost_TwoDimensionOverload_UnchangedByCacheAdditions()
    {
        var price = new ModelPrice(3.00m, 15.00m);

        var cost = price.EstimateCost(1_000_000, 1_000_000);

        Assert.Equal(18.00m, actual: cost);
    }

    [Fact]
    public void Free_HasZeroCacheRatesToo()
    {
        var usage = new UsageInfo(1_000_000, 1_000_000, 1_000_000, 1_000_000);

        var cost = ModelPrice.Free.EstimateCost(usage);

        Assert.Equal(0m, actual: cost);
    }

    [Fact]
    public void EstimateCost_OutFlag_FullCacheRates_ReportsNoFallback()
    {
        var price = new ModelPrice(3.00m, 15.00m, 0.30m, 3.75m);
        var usage = new UsageInfo(0, 0, 1_000_000, 1_000_000);

        price.EstimateCost(usage: usage, usedCacheRateFallback: out var usedFallback);

        Assert.False(usedFallback);
    }

    [Fact]
    public void EstimateCost_OutFlag_MissingCacheRatesWithNonzeroCacheTokens_ReportsFallback()
    {
        var price = new ModelPrice(3.00m, 15.00m);
        var usage = new UsageInfo(0, 0, 1_000);

        price.EstimateCost(usage: usage, usedCacheRateFallback: out var usedFallback);

        Assert.True(usedFallback);
    }

    [Fact]
    public void EstimateCost_OutFlag_MissingCacheRatesButZeroCacheTokens_ReportsNoFallback()
    {
        // No cache dimension was actually rated, so nothing "fell back" - a cache rate that's merely
        // unpublished must not taint an otherwise-exact price when the request used no cache at all.
        var price = new ModelPrice(3.00m, 15.00m);
        var usage = new UsageInfo(1_000_000, 1_000_000);

        price.EstimateCost(usage: usage, usedCacheRateFallback: out var usedFallback);

        Assert.False(usedFallback);
    }

    [Fact]
    public void EstimateCost_UsageInfo_ReasoningTokensDoNotChangeCost()
    {
        // ReasoningTokens is a subset of CompletionTokens (UsageInfo's doc contract), so it must not be
        // priced as a fifth, additive dimension - the cost is identical whether it's populated or zero.
        var price = new ModelPrice(3.00m, 15.00m);
        var withoutReasoning = new UsageInfo(1_000, 2_000);
        var withReasoning = new UsageInfo(1_000, 2_000, ReasoningTokens: 1_500);

        Assert.Equal(expected: price.EstimateCost(withoutReasoning), actual: price.EstimateCost(withReasoning));
    }

    [Fact]
    public void IsApproximateMatch_DefaultsToFalse()
    {
        var price = new ModelPrice(3.00m, 15.00m);

        Assert.False(price.IsApproximateMatch);
    }
}