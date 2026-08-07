using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="ProviderBudgetStore"/>: cap persistence, current-month spend accumulation, the
/// dollar-and-token breach rule that routing enforcement reads, and the repository round-trip that backs it.
/// </summary>
public class ProviderBudgetStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset FixedUsageAt = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetStatus_UnbudgetedProvider_IsAllZeroAndNotBreached()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();

        var status = store.GetStatus("openai");

        Assert.Null(status.DollarCap);
        Assert.Null(status.TokenCap);
        Assert.Equal(0m, status.DollarSpent);
        Assert.Equal(0L, status.TokensUsed);
        Assert.Equal(0L, status.CacheTokensUsed);
        Assert.Null(status.LastUsageAtUtc);
        Assert.False(status.IsBreached);
        Assert.False(store.IsBreached("openai"));
    }

    [Fact]
    public void SetBudget_PersistsAcrossAFreshStore()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        var store = temp.CreateBudgetStore(repository);
        store.SetBudget("openai", dollarCap: 500m, tokenCap: 1_000_000L);

        // A second store over the same database stands in for a restart: SQLite owns the caps.
        var reopened = temp.CreateBudgetStore(repository);
        var status = reopened.GetStatus("openai");
        Assert.Equal(500m, status.DollarCap);
        Assert.Equal(1_000_000L, status.TokenCap);
    }

    [Fact]
    public void SetBudget_BothNull_RemovesTheBudget()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var store = temp.CreateBudgetStore(repository);

        store.SetBudget("openai", dollarCap: 500m, tokenCap: null);
        store.SetBudget("openai", dollarCap: null, tokenCap: null);

        Assert.Empty(repository.GetProviderBudgets());
        Assert.Null(store.GetStatus("openai").DollarCap);
    }

    [Fact]
    public void SetBudget_RaisesChanged()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        var raised = 0;
        store.Changed += () => raised++;

        store.SetBudget("openai", dollarCap: 100m, tokenCap: null);

        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, -1)]
    public void SetBudget_NegativeCap_ThrowsAndPersistsNothing(int? dollar, int? token)
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var store = temp.CreateBudgetStore(repository);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetBudget("openai", dollar, token));

        Assert.Empty(repository.GetProviderBudgets());
    }

    [Fact]
    public async Task RecordUsage_AccumulatesSpendForTheCurrentMonth()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();

        await store.RecordUsageAsync("openai", costUsd: 1.50m, promptTokens: 100, completionTokens: 40, cacheCreationTokens: null, cacheReadTokens: null, FixedUsageAt, Ct);
        await store.RecordUsageAsync("openai", costUsd: 0.25m, promptTokens: 10, completionTokens: 5, cacheCreationTokens: null, cacheReadTokens: null, FixedUsageAt, Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(1.75m, status.DollarSpent);
        Assert.Equal(155L, status.TokensUsed);
    }

    [Fact]
    public async Task RecordUsage_NullCostAndTokens_ContributeZero()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();

        await store.RecordUsageAsync("openai", costUsd: null, promptTokens: null, completionTokens: null, cacheCreationTokens: null, cacheReadTokens: null, FixedUsageAt, Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(0m, status.DollarSpent);
        Assert.Equal(0L, status.TokensUsed);
    }

    [Fact]
    public async Task Spend_IsScopedToItsPeriod_SoLastMonthDoesNotCountAgainstThisMonth()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        // Write spend directly into a prior period; the store only ever reads the current month.
        repository.AddProviderSpend("openai", "2000-01", 999m, 1_000, 1_000, cacheCreationTokens: 0, cacheReadTokens: 0, FixedUsageAt);

        var store = temp.CreateBudgetStore(repository);
        await store.RecordUsageAsync("openai", costUsd: 2m, promptTokens: 3, completionTokens: 4, cacheCreationTokens: null, cacheReadTokens: null, FixedUsageAt, Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(2m, status.DollarSpent);
        Assert.Equal(7L, status.TokensUsed);
    }

    [Fact]
    public async Task IsBreached_TrueWhenDollarCapMet()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget("openai", dollarCap: 10m, tokenCap: null);

        Assert.False(store.IsBreached("openai"));
        await store.RecordUsageAsync("openai", costUsd: 10m, promptTokens: 0, completionTokens: 0, cacheCreationTokens: null, cacheReadTokens: null, FixedUsageAt, Ct);

        Assert.True(store.IsBreached("openai"));
    }

    [Fact]
    public async Task IsBreached_TrueWhenTokenCapMet_EvenAtZeroCost()
    {
        // A free provider bills $0 but still consumes tokens; a token cap must still be able to breach it.
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget("ollama", dollarCap: null, tokenCap: 100L);

        await store.RecordUsageAsync("ollama", costUsd: 0m, promptTokens: 60, completionTokens: 45, cacheCreationTokens: null, cacheReadTokens: null, FixedUsageAt, Ct);

        Assert.True(store.IsBreached("ollama"));
    }

    [Fact]
    public async Task IsBreached_FalseWhenBothCapsSetButNeitherMet()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget("openai", dollarCap: 100m, tokenCap: 1_000L);

        await store.RecordUsageAsync("openai", costUsd: 99.99m, promptTokens: 500, completionTokens: 499, cacheCreationTokens: null, cacheReadTokens: null, FixedUsageAt, Ct);

        Assert.False(store.IsBreached("openai"));
    }

    [Fact]
    public async Task IsBreached_TrueWhenTokenCapMetOnlyViaCacheTokens()
    {
        // Cache tokens count toward the token cap (the deliberate widening) - a request with zero prompt/
        // completion tokens but heavy cache usage must still be able to breach a token cap.
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget("openai", dollarCap: null, tokenCap: 100L);

        await store.RecordUsageAsync("openai", costUsd: 0m, promptTokens: 0, completionTokens: 0, cacheCreationTokens: 60, cacheReadTokens: 45, FixedUsageAt, Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(105L, status.TokensUsed);
        Assert.Equal(105L, status.CacheTokensUsed);
        Assert.True(store.IsBreached("openai"));
    }

    [Fact]
    public async Task RecordUsage_AccumulatesCacheTokensSeparatelyFromPromptAndCompletion()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();

        await store.RecordUsageAsync("openai", costUsd: 1m, promptTokens: 10, completionTokens: 5, cacheCreationTokens: 200, cacheReadTokens: 300, FixedUsageAt, Ct);
        await store.RecordUsageAsync("openai", costUsd: 1m, promptTokens: 10, completionTokens: 5, cacheCreationTokens: 20, cacheReadTokens: 30, FixedUsageAt, Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(550L, status.CacheTokensUsed);
        Assert.Equal(580L, status.TokensUsed);
    }

    [Fact]
    public async Task RecordUsage_UpdatesLastUsageAtUtc_OnTheFastPath()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        var firstUsageAt = FixedUsageAt;
        var secondUsageAt = FixedUsageAt.AddMinutes(5);

        await store.RecordUsageAsync("openai", costUsd: 1m, promptTokens: 1, completionTokens: 1, cacheCreationTokens: null, cacheReadTokens: null, firstUsageAt, Ct);
        Assert.Equal(firstUsageAt, store.GetStatus("openai").LastUsageAtUtc);

        await store.RecordUsageAsync("openai", costUsd: 1m, promptTokens: 1, completionTokens: 1, cacheCreationTokens: null, cacheReadTokens: null, secondUsageAt, Ct);
        Assert.Equal(secondUsageAt, store.GetStatus("openai").LastUsageAtUtc);
    }

    [Fact]
    public async Task RecordUsage_LastUsageAtUtc_SurvivesReload()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var store = temp.CreateBudgetStore(repository);

        await store.RecordUsageAsync("openai", costUsd: 1m, promptTokens: 1, completionTokens: 1, cacheCreationTokens: null, cacheReadTokens: null, FixedUsageAt, Ct);

        var reopened = temp.CreateBudgetStore(repository);
        Assert.Equal(FixedUsageAt, reopened.GetStatus("openai").LastUsageAtUtc);
    }
}
