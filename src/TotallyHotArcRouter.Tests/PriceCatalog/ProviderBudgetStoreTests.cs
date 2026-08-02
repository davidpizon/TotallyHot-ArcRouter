using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="ProviderBudgetStore"/>: cap persistence, current-month spend accumulation, the
/// dollar-and-token breach rule that routing enforcement reads, and the repository round-trip that backs it.
/// </summary>
public class ProviderBudgetStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

        await store.RecordUsageAsync("openai", costUsd: 1.50m, promptTokens: 100, completionTokens: 40, Ct);
        await store.RecordUsageAsync("openai", costUsd: 0.25m, promptTokens: 10, completionTokens: 5, Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(1.75m, status.DollarSpent);
        Assert.Equal(155L, status.TokensUsed);
    }

    [Fact]
    public async Task RecordUsage_NullCostAndTokens_ContributeZero()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();

        await store.RecordUsageAsync("openai", costUsd: null, promptTokens: null, completionTokens: null, Ct);

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
        repository.AddProviderSpend("openai", "2000-01", 999m, 1_000, 1_000);

        var store = temp.CreateBudgetStore(repository);
        await store.RecordUsageAsync("openai", costUsd: 2m, promptTokens: 3, completionTokens: 4, Ct);

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
        await store.RecordUsageAsync("openai", costUsd: 10m, promptTokens: 0, completionTokens: 0, Ct);

        Assert.True(store.IsBreached("openai"));
    }

    [Fact]
    public async Task IsBreached_TrueWhenTokenCapMet_EvenAtZeroCost()
    {
        // A free provider bills $0 but still consumes tokens; a token cap must still be able to breach it.
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget("ollama", dollarCap: null, tokenCap: 100L);

        await store.RecordUsageAsync("ollama", costUsd: 0m, promptTokens: 60, completionTokens: 45, Ct);

        Assert.True(store.IsBreached("ollama"));
    }

    [Fact]
    public async Task IsBreached_FalseWhenBothCapsSetButNeitherMet()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget("openai", dollarCap: 100m, tokenCap: 1_000L);

        await store.RecordUsageAsync("openai", costUsd: 99.99m, promptTokens: 500, completionTokens: 499, Ct);

        Assert.False(store.IsBreached("openai"));
    }
}

