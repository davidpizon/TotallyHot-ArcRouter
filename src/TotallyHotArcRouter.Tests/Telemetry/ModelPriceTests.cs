using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="ModelPrice.EstimateCost"/>.</summary>
public class ModelPriceTests
{
    [Fact]
    public void EstimateCost_ComputesPerMillionTokenRatesCorrectly()
    {
        var price = new ModelPrice(InputPerMillionTokens: 3.00m, OutputPerMillionTokens: 15.00m);

        var cost = price.EstimateCost(promptTokens: 1_000_000, completionTokens: 1_000_000);

        Assert.Equal(18.00m, cost);
    }

    [Fact]
    public void EstimateCost_FractionOfAMillionTokens_ScalesLinearly()
    {
        var price = new ModelPrice(InputPerMillionTokens: 2.00m, OutputPerMillionTokens: 10.00m);

        // 500,000 prompt tokens = half of $2.00 = $1.00; 100,000 completion tokens = 1/10 of $10.00 = $1.00.
        var cost = price.EstimateCost(promptTokens: 500_000, completionTokens: 100_000);

        Assert.Equal(2.00m, cost);
    }

    [Fact]
    public void EstimateCost_ZeroTokens_IsZero()
    {
        var price = new ModelPrice(InputPerMillionTokens: 5.00m, OutputPerMillionTokens: 20.00m);

        var cost = price.EstimateCost(0, 0);

        Assert.Equal(0m, cost);
    }

    [Fact]
    public void EstimateCost_UsageInfo_WithFullCacheRates_PricesEachComponentAtItsOwnRate()
    {
        var price = new ModelPrice(
            InputPerMillionTokens: 3.00m,
            OutputPerMillionTokens: 15.00m,
            CacheReadPerMillionTokens: 0.30m,
            CacheWritePerMillionTokens: 3.75m);
        var usage = new UsageInfo(PromptTokens: 1_000_000, CompletionTokens: 1_000_000, CacheCreationTokens: 1_000_000, CacheReadTokens: 1_000_000);

        var cost = price.EstimateCost(usage);

        Assert.Equal(3.00m + 15.00m + 0.30m + 3.75m, cost);
    }

    [Fact]
    public void EstimateCost_UsageInfo_MissingCacheRates_FallsBackToStandardInputRate()
    {
        var price = new ModelPrice(InputPerMillionTokens: 3.00m, OutputPerMillionTokens: 15.00m);
        var usage = new UsageInfo(PromptTokens: 0, CompletionTokens: 0, CacheCreationTokens: 1_000_000, CacheReadTokens: 1_000_000);

        var cost = price.EstimateCost(usage);

        // Both cache dimensions fall back to InputPerMillionTokens (3.00) when the catalog has no published
        // cache rate - the conservative overestimate documented on ModelPrice.EstimateCost(UsageInfo).
        Assert.Equal(6.00m, cost);
    }

    [Fact]
    public void EstimateCost_TwoDimensionOverload_UnchangedByCacheAdditions()
    {
        var price = new ModelPrice(InputPerMillionTokens: 3.00m, OutputPerMillionTokens: 15.00m);

        var cost = price.EstimateCost(promptTokens: 1_000_000, completionTokens: 1_000_000);

        Assert.Equal(18.00m, cost);
    }

    [Fact]
    public void Free_HasZeroCacheRatesToo()
    {
        var usage = new UsageInfo(PromptTokens: 1_000_000, CompletionTokens: 1_000_000, CacheCreationTokens: 1_000_000, CacheReadTokens: 1_000_000);

        var cost = ModelPrice.Free.EstimateCost(usage);

        Assert.Equal(0m, cost);
    }
}

