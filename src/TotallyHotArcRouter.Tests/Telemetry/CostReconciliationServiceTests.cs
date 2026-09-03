using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="CostReconciliationService"/>'s cycle logic: checkpoint progression, the catch-up
/// cap, and stopping at the first failed day (§5.8).
/// </summary>
public class CostReconciliationServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Yesterday => DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));

    private static CostReconciliationService BuildService(
        TempDatabase temp,
        IEnumerable<IProviderCostReconciler> reconcilers,
        decimal deltaWarningPercent = 20m) =>
        new(
            reconcilers,
            temp.CreateUsageLedger(),
            temp.CreateCostReconciliationStore(),
            Options.Create(new CostReconciliationOptions { DeltaWarningPercent = deltaWarningPercent }),
            NullLogger<CostReconciliationService>.Instance);

    [Fact]
    public async Task RunCycleAsync_NoReconcilers_DoesNothingAndDoesNotThrow()
    {
        using var temp = new TempDatabase();
        var service = BuildService(temp, []);

        await service.RunCycleAsync(Ct);
    }

    [Fact]
    public async Task RunCycleAsync_FirstRun_ReconcilesOnlyYesterday_AndAdvancesCheckpoint()
    {
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler("openai", _ => 5m);
        var store = temp.CreateCostReconciliationStore();
        var service = new CostReconciliationService(
            [reconciler],
            temp.CreateUsageLedger(),
            store,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance);

        await service.RunCycleAsync(Ct);

        Assert.Equal(Yesterday, store.GetLastReconciledDay("openai"));
        Assert.Single(reconciler.CalledDays);
        Assert.Equal(Yesterday, reconciler.CalledDays[0]);
    }

    [Fact]
    public async Task RunCycleAsync_AlreadyCaughtUp_DoesNotCallReconcilerAgain()
    {
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler("openai", _ => 5m);
        var store = temp.CreateCostReconciliationStore();
        store.SetLastReconciledDay("openai", Yesterday);
        var service = new CostReconciliationService(
            [reconciler],
            temp.CreateUsageLedger(),
            store,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance);

        await service.RunCycleAsync(Ct);

        Assert.Empty(reconciler.CalledDays);
    }

    [Fact]
    public async Task RunCycleAsync_CheckpointTwoDaysBehind_ReconcilesBothMissingDaysInOrder()
    {
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler("openai", _ => 1m);
        var store = temp.CreateCostReconciliationStore();
        store.SetLastReconciledDay("openai", Yesterday.AddDays(-2));
        var service = new CostReconciliationService(
            [reconciler],
            temp.CreateUsageLedger(),
            store,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance);

        await service.RunCycleAsync(Ct);

        Assert.Equal([Yesterday.AddDays(-1), Yesterday], reconciler.CalledDays);
        Assert.Equal(Yesterday, store.GetLastReconciledDay("openai"));
    }

    [Fact]
    public async Task RunCycleAsync_CheckpointFarBehind_CapsCatchUpAtMaxCatchUpDays()
    {
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler("openai", _ => 1m);
        var store = temp.CreateCostReconciliationStore();
        store.SetLastReconciledDay("openai", Yesterday.AddDays(-100));
        var service = new CostReconciliationService(
            [reconciler],
            temp.CreateUsageLedger(),
            store,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance);

        await service.RunCycleAsync(Ct);

        Assert.Equal(CostReconciliationService.MaxCatchUpDays, reconciler.CalledDays.Count);
        Assert.Equal(Yesterday, reconciler.CalledDays[^1]);
    }

    [Fact]
    public async Task RunCycleAsync_ReconcilerThrowsOnSecondDay_StopsAndDoesNotAdvanceCheckpointPastFailure()
    {
        using var temp = new TempDatabase();
        var failDay = Yesterday;
        var reconciler = new FakeCostReconciler("openai", day => day == failDay ? throw new HttpRequestException("boom") : 1m);
        var store = temp.CreateCostReconciliationStore();
        store.SetLastReconciledDay("openai", Yesterday.AddDays(-2));
        var service = new CostReconciliationService(
            [reconciler],
            temp.CreateUsageLedger(),
            store,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance);

        await service.RunCycleAsync(Ct);

        // Day -1 succeeded and advanced the checkpoint; "yesterday" (failDay) threw, so the checkpoint
        // must stop at -1, not skip past the failure.
        Assert.Equal(Yesterday.AddDays(-1), store.GetLastReconciledDay("openai"));
    }

    [Fact]
    public async Task RunCycleAsync_ReconcilerFactorySupplied_IsInvokedFreshEachCycle_InsteadOfTheFixedList()
    {
        // Simulates an Admin API key saved from the GUI between two cycles (docs/router/secrets-at-rest-plan.md
        // §7): the constructor's fixed reconciler list is empty, but the factory starts returning a
        // reconciler on the second call - proving the fixed list is not what RunCycleAsync uses when a
        // factory is supplied.
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler("anthropic", _ => 3m);
        var store = temp.CreateCostReconciliationStore();
        var calls = 0;
        IReadOnlyList<IProviderCostReconciler> Factory()
        {
            calls++;
            return calls == 1 ? [] : [reconciler];
        }

        var service = new CostReconciliationService(
            [],
            temp.CreateUsageLedger(),
            store,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance,
            Factory);

        await service.RunCycleAsync(Ct);
        Assert.Empty(reconciler.CalledDays);

        await service.RunCycleAsync(Ct);
        Assert.Single(reconciler.CalledDays);
    }

    [Fact]
    public async Task RunCycleAsync_NoReconcilerFactorySupplied_UsesTheFixedConstructorList()
    {
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler("openai", _ => 5m);
        var store = temp.CreateCostReconciliationStore();
        var service = new CostReconciliationService(
            [reconciler],
            temp.CreateUsageLedger(),
            store,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance);

        await service.RunCycleAsync(Ct);

        Assert.Single(reconciler.CalledDays);
    }

    [Fact]
    public async Task RunCycleAsync_InsertsReconciliationRowWithReportedAndLocalCost()
    {
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler("openai", _ => 42m);
        var costStore = temp.CreateCostReconciliationStore();
        var service = new CostReconciliationService(
            [reconciler],
            temp.CreateUsageLedger(),
            costStore,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance);

        await service.RunCycleAsync(Ct);

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider, provider_reported_cost_usd, local_estimated_cost_usd FROM provider_cost_reconciliation;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("openai", reader.GetString(0));
        Assert.Equal("42", reader.GetString(1));
        Assert.Equal("0", reader.GetString(2)); // no local usage_ledger data seeded for this window
    }

    [Fact]
    public async Task RunCycleAsync_MultipleReconcilers_EachTrackedIndependently()
    {
        using var temp = new TempDatabase();
        var openAi = new FakeCostReconciler("openai", _ => 1m);
        var anthropic = new FakeCostReconciler("anthropic", _ => 2m);
        var store = temp.CreateCostReconciliationStore();
        var service = new CostReconciliationService(
            [openAi, anthropic],
            temp.CreateUsageLedger(),
            store,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance);

        await service.RunCycleAsync(Ct);

        Assert.Equal(Yesterday, store.GetLastReconciledDay("openai"));
        Assert.Equal(Yesterday, store.GetLastReconciledDay("anthropic"));
        Assert.Single(openAi.CalledDays);
        Assert.Single(anthropic.CalledDays);
    }

    [Fact]
    public async Task RunCycleAsync_LocalCostQueriedDirectlyFromLedger_UnaffectedByNonUtcRollupTimezone()
    {
        // A P1D usage_rollup bucket's boundaries are computed in the pinned Storage:RollupTimezone, not
        // UTC (see UsageRollupStore.BucketStartUtc) - if local cost were still read from that rollup store
        // with a plain UTC-midnight window, a non-UTC timezone would make this query miss the bucket
        // entirely and silently report localCost = 0 despite real local usage existing for that UTC day.
        // Seeding a non-UTC rollup store here (never queried by the fix) alongside a ledger entry proves
        // the reconciliation result no longer depends on it.
        using var temp = new TempDatabase();
        _ = temp.CreateRollupStore(rollupTimezone: "America/New_York");
        var ledger = temp.CreateUsageLedger();
        await ledger.RecordAsync(new UsageLedgerEntry(
            SessionId: "s1",
            TurnNumber: 1,
            Provider: "openai",
            RequestedModel: "gpt-5",
            ResolvedModel: "gpt-5",
            PromptTokens: 10,
            CompletionTokens: 5,
            CacheCreationTokens: 0,
            CacheReadTokens: 0,
            EstimatedCostUsd: 7.5m,
            CostConfidence: CostConfidence.Catalog,
            OccurredAtUtc: Yesterday.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)), Ct);

        var reconciler = new FakeCostReconciler("openai", _ => 42m);
        var costStore = temp.CreateCostReconciliationStore();
        var service = new CostReconciliationService(
            [reconciler],
            ledger,
            costStore,
            Options.Create(new CostReconciliationOptions()),
            NullLogger<CostReconciliationService>.Instance);

        await service.RunCycleAsync(Ct);

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_estimated_cost_usd FROM provider_cost_reconciliation;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("7.5", reader.GetString(0));
    }

    [Fact]
    public async Task RunCycleAsync_LocalCostZero_ReportedCostPositive_DoesNotLogWarning()
    {
        // localCost == 0 while reportedCost > 0 is the documented, legitimate "this proxy routed no
        // traffic for the provider that day" gap (see the persisted entry's ScopeNote) - naively computing
        // deltaPercent against a zero local base would always read as a 100% difference and misfire a
        // "price table may be stale" warning on every occurrence of an expected scope mismatch.
        using var temp = new TempDatabase();
        var reconciler = new FakeCostReconciler("openai", _ => 42m);
        var logger = new CapturingLogger<CostReconciliationService>();
        var service = new CostReconciliationService(
            [reconciler],
            temp.CreateUsageLedger(),
            temp.CreateCostReconciliationStore(),
            Options.Create(new CostReconciliationOptions()),
            logger);

        await service.RunCycleAsync(Ct);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    private sealed class FakeCostReconciler(string provider, Func<DateOnly, decimal> costForDay) : IProviderCostReconciler
    {
        public List<DateOnly> CalledDays { get; } = [];

        public string Provider => provider;

        public Task<decimal> GetReportedCostAsync(DateOnly day, CancellationToken cancellationToken = default)
        {
            CalledDays.Add(day);
            return Task.FromResult(costForDay(day));
        }
    }

    /// <summary>Minimal <see cref="ILogger{TCategoryName}"/> test double that records each entry's level.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
