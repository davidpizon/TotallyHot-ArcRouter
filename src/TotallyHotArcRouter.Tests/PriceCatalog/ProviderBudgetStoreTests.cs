using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="ProviderBudgetStore"/>: cap persistence, current-month spend accumulation, the
/// dollar-and-token breach rule that routing enforcement reads, and the repository round-trip that backs it.
/// </summary>
public class ProviderBudgetStoreTests
{
    private static readonly DateTimeOffset FixedUsageAt = new(2026, 3, 1, 12, 0, 0, offset: TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void GetStatus_UnbudgetedProvider_IsAllZeroAndNotBreached()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();

        var status = store.GetStatus("openai");

        Assert.Null(status.DollarCap);
        Assert.Null(status.TokenCap);
        Assert.Equal(0m, actual: status.DollarSpent);
        Assert.Equal(0L, actual: status.TokensUsed);
        Assert.Equal(0L, actual: status.CacheTokensUsed);
        Assert.Null(status.LastUsageAtUtc);
        Assert.False(status.IsBreached);
        Assert.False(store.IsBreached("openai"));
    }

    [Fact]
    public void SetBudget_PersistsAcrossAFreshStore()
    {
        using var temp = new TempDatabase();
        var budgetRepository = temp.CreateBudgetRepository();
        var spendRepository = temp.CreateSpendRepository();

        var store = temp.CreateBudgetStore(budgetRepository: budgetRepository, spendRepository: spendRepository);
        store.SetBudget(providerKey: "openai", 500m, 1_000_000L);

        // A second store over the same database stands in for a restart: SQLite owns the caps.
        var reopened = temp.CreateBudgetStore(budgetRepository: budgetRepository, spendRepository: spendRepository);
        var status = reopened.GetStatus("openai");
        Assert.Equal(500m, actual: status.DollarCap);
        Assert.Equal(1_000_000L, actual: status.TokenCap);
    }

    [Fact]
    public void SetBudget_BothNull_RemovesTheBudget()
    {
        using var temp = new TempDatabase();
        var budgetRepository = temp.CreateBudgetRepository();
        var store = temp.CreateBudgetStore(budgetRepository);

        store.SetBudget(providerKey: "openai", 500m, null);
        store.SetBudget(providerKey: "openai", null, null);

        Assert.Empty(budgetRepository.GetProviderBudgets());
        Assert.Null(store.GetStatus("openai").DollarCap);
    }

    [Fact]
    public void SetBudget_RaisesChanged()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        var raised = 0;
        store.Changed += () => raised++;

        store.SetBudget(providerKey: "openai", 100m, null);

        Assert.Equal(1, actual: raised);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, -1)]
    public void SetBudget_NegativeCap_ThrowsAndPersistsNothing(int? dollar, int? token)
    {
        using var temp = new TempDatabase();
        var budgetRepository = temp.CreateBudgetRepository();
        var store = temp.CreateBudgetStore(budgetRepository);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.SetBudget(providerKey: "openai", dollarCap: dollar, tokenCap: token));

        Assert.Empty(budgetRepository.GetProviderBudgets());
    }

    [Fact]
    public async Task RecordUsage_AccumulatesSpendForTheCurrentMonth()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();

        await store.RecordUsageAsync(providerKey: "openai", 1.50m, 100, 40, null, null, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);
        await store.RecordUsageAsync(providerKey: "openai", 0.25m, 10, 5, null, null, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(1.75m, actual: status.DollarSpent);
        Assert.Equal(155L, actual: status.TokensUsed);
    }

    [Fact]
    public async Task RecordUsage_NullCostAndTokens_ContributeZero()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();

        await store.RecordUsageAsync(providerKey: "openai", null, null, null, null, null, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(0m, actual: status.DollarSpent);
        Assert.Equal(0L, actual: status.TokensUsed);
    }

    [Fact]
    public async Task Spend_IsScopedToItsPeriod_SoLastMonthDoesNotCountAgainstThisMonth()
    {
        using var temp = new TempDatabase();
        var spendRepository = temp.CreateSpendRepository();

        // Write spend directly into a prior period; the store only ever reads the current month.
        spendRepository.AddProviderSpend(providerKey: "openai", period: "2000-01", 999m, 1_000, 1_000, 0, 0,
            usageAtUtc: FixedUsageAt);

        var store = temp.CreateBudgetStore(spendRepository: spendRepository);
        await store.RecordUsageAsync(providerKey: "openai", 2m, 3, 4, null, null, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(2m, actual: status.DollarSpent);
        Assert.Equal(7L, actual: status.TokensUsed);
    }

    [Fact]
    public async Task IsBreached_TrueWhenDollarCapMet()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget(providerKey: "openai", 10m, null);

        Assert.False(store.IsBreached("openai"));
        await store.RecordUsageAsync(providerKey: "openai", 10m, 0, 0, null, null, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);

        Assert.True(store.IsBreached("openai"));
    }

    [Fact]
    public async Task IsBreached_TrueWhenTokenCapMet_EvenAtZeroCost()
    {
        // A free provider bills $0 but still consumes tokens; a token cap must still be able to breach it.
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget(providerKey: "ollama", null, 100L);

        await store.RecordUsageAsync(providerKey: "ollama", 0m, 60, 45, null, null, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);

        Assert.True(store.IsBreached("ollama"));
    }

    [Fact]
    public async Task IsBreached_FalseWhenBothCapsSetButNeitherMet()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget(providerKey: "openai", 100m, 1_000L);

        await store.RecordUsageAsync(providerKey: "openai", 99.99m, 500, 499, null, null, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);

        Assert.False(store.IsBreached("openai"));
    }

    [Fact]
    public async Task IsBreached_TrueWhenTokenCapMetOnlyViaCacheTokens()
    {
        // Cache tokens count toward the token cap (the deliberate widening) - a request with zero prompt/
        // completion tokens but heavy cache usage must still be able to breach a token cap.
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        store.SetBudget(providerKey: "openai", null, 100L);

        await store.RecordUsageAsync(providerKey: "openai", 0m, 0, 0, 60, 45, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(105L, actual: status.TokensUsed);
        Assert.Equal(105L, actual: status.CacheTokensUsed);
        Assert.True(store.IsBreached("openai"));
    }

    [Fact]
    public async Task RecordUsage_AccumulatesCacheTokensSeparatelyFromPromptAndCompletion()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();

        await store.RecordUsageAsync(providerKey: "openai", 1m, 10, 5, 200, 300, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);
        await store.RecordUsageAsync(providerKey: "openai", 1m, 10, 5, 20, 30, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);

        var status = store.GetStatus("openai");
        Assert.Equal(550L, actual: status.CacheTokensUsed);
        Assert.Equal(580L, actual: status.TokensUsed);
    }

    [Fact]
    public async Task RecordUsage_UpdatesLastUsageAtUtc_OnTheFastPath()
    {
        using var temp = new TempDatabase();
        var store = temp.CreateBudgetStore();
        var firstUsageAt = FixedUsageAt;
        var secondUsageAt = FixedUsageAt.AddMinutes(5);

        await store.RecordUsageAsync(providerKey: "openai", 1m, 1, 1, null, null, usageAtUtc: firstUsageAt,
            cancellationToken: Ct);
        Assert.Equal(expected: firstUsageAt, actual: store.GetStatus("openai").LastUsageAtUtc);

        await store.RecordUsageAsync(providerKey: "openai", 1m, 1, 1, null, null, usageAtUtc: secondUsageAt,
            cancellationToken: Ct);
        Assert.Equal(expected: secondUsageAt, actual: store.GetStatus("openai").LastUsageAtUtc);
    }

    [Fact]
    public async Task RecordUsage_LastUsageAtUtc_SurvivesReload()
    {
        using var temp = new TempDatabase();
        var budgetRepository = temp.CreateBudgetRepository();
        var spendRepository = temp.CreateSpendRepository();
        var store = temp.CreateBudgetStore(budgetRepository: budgetRepository, spendRepository: spendRepository);

        await store.RecordUsageAsync(providerKey: "openai", 1m, 1, 1, null, null, usageAtUtc: FixedUsageAt,
            cancellationToken: Ct);

        var reopened = temp.CreateBudgetStore(budgetRepository: budgetRepository, spendRepository: spendRepository);
        Assert.Equal(expected: FixedUsageAt, actual: reopened.GetStatus("openai").LastUsageAtUtc);
    }
}