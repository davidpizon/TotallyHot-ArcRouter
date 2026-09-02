using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="ProviderSpendRepository"/>'s spend accounting.</summary>
public class ProviderSpendRepositoryTests
{
    [Fact]
    public void AddProviderSpend_RepeatedCalls_AccumulatesCacheTokensAndAdvancesLastUsageAt()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateSpendRepository();
        var firstUsageAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var secondUsageAt = firstUsageAt.AddHours(1);

        repository.AddProviderSpend("anthropic", "2026-03", 1m, 10, 5, cacheCreationTokens: 100, cacheReadTokens: 200, usageAtUtc: firstUsageAt);
        repository.AddProviderSpend("anthropic", "2026-03", 2m, 20, 10, cacheCreationTokens: 50, cacheReadTokens: 25, usageAtUtc: secondUsageAt);

        var row = Assert.Single(repository.GetProviderSpend("2026-03"));
        Assert.Equal(150L, row.CacheCreationTokens);
        Assert.Equal(225L, row.CacheReadTokens);
        Assert.Equal(secondUsageAt, row.LastUsageAtUtc);
    }
}
