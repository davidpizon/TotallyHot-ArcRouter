using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="PriceCatalogIngestionService.RunCycleAsync"/>.</summary>
public class PriceCatalogIngestionServiceTests
{
    [Fact]
    public async Task RunCycleAsync_SourceSucceeds_ReportsFreshPrices()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var registry = new FakeRegistry(new StubSource("litellm", new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null)));
        var service = Build(registry, repository, sourceRepository, toggleStore);

        var summary = await service.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.FreshPriceCount);
        var outcome = Assert.Single(summary.Outcomes);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.PriceCount);
    }

    [Fact]
    public void ScheduleAnchor_IsSeededBeforeAnyCycleHasRun()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var before = DateTimeOffset.UtcNow;

        var service = Build(new FakeRegistry(), repository, sourceRepository, toggleStore);

        // "Never ran" must not read as "overdue": a router with every source disabled skips the startup cycle
        // entirely, and an unseeded anchor would make the poll loop fire the instant it started.
        Assert.InRange(service.ScheduleAnchorUtc, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task RunCycleAsync_ReanchorsTheSchedule()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var registry = new FakeRegistry(new StubSource("litellm", new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null)));
        var service = Build(registry, repository, sourceRepository, toggleStore);
        var seeded = service.ScheduleAnchorUtc;
        await Task.Delay(10, TestContext.Current.CancellationToken);

        await service.RunCycleAsync(TestContext.Current.CancellationToken);

        // Every cycle re-anchors, whoever ran it. This is the single fact behind both the poll loop's timing
        // and the panel's countdown, which is why a manual pull resets the clock.
        Assert.True(service.ScheduleAnchorUtc > seeded);
    }

    [Fact]
    public async Task RunCycleAsync_ReanchorsEvenWhenEverySourceFails()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var service = Build(new FakeRegistry(new ThrowingSource("litellm")), repository, sourceRepository, toggleStore);
        var seeded = service.ScheduleAnchorUtc;
        await Task.Delay(10, TestContext.Current.CancellationToken);

        var summary = await service.RunCycleAsync(TestContext.Current.CancellationToken);

        // The cycle consumed the interval whether or not it wrote anything. An anchor that only moved on a
        // successful write would retry a persistently failing feed in a tight loop - and would leave the
        // panel's countdown running negative for the whole of an outage, since no source's last_updated_utc
        // moves here either.
        Assert.Equal(0, summary.FreshPriceCount);
        Assert.True(service.ScheduleAnchorUtc > seeded);
    }

    [Fact]
    public async Task RunCycleAsync_SourceFails_ReportsZeroFreshForCheckFour()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var registry = new FakeRegistry(new ThrowingSource("litellm"));
        var service = Build(registry, repository, sourceRepository, toggleStore);

        var summary = await service.RunCycleAsync(TestContext.Current.CancellationToken);

        // A failed cycle leaves nothing fresh - the condition the startup check's Error (D4) fires on.
        Assert.Equal(0, summary.FreshPriceCount);
        var outcome = Assert.Single(summary.Outcomes);
        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task RunCycleAsync_NoEnabledSources_ReportsEmptyCycle()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var service = Build(new FakeRegistry(), repository, sourceRepository, toggleStore);

        var summary = await service.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Empty(summary.Outcomes);
        Assert.Equal(0, summary.FreshPriceCount);
    }

    [Fact]
    public async Task RunCycleAsync_SourceDisabledMidFetch_CancelsAndRecordsOutcome()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);

        // Disable the source from "the panel" once its fetch is genuinely in flight.
        var source = new BlockingSource("litellm", observeCancellation: true, new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null));
        var service = Build(new FakeRegistry(source), repository, sourceRepository, toggleStore);

        var cycle = service.RunCycleAsync(TestContext.Current.CancellationToken);
        await source.FetchStarted.Task;
        toggleStore.SetEnabled("litellm", enabled: false);

        var summary = await cycle;

        var outcome = Assert.Single(summary.Outcomes);
        Assert.False(outcome.Succeeded);
        Assert.Equal(PriceCatalogIngestionService.DisabledDuringFetch, outcome.Error);

        // Nothing lands: a source switched off must stop influencing the catalog immediately, not from the
        // next cycle (D6).
        Assert.Equal(0, sourceRepository.CountFreshPrices(TimeSpan.FromHours(24)));
    }

    [Fact]
    public async Task RunCycleAsync_SourceDisabledAfterFetchCompletes_DiscardsTheUpsert()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);

        // This source ignores its cancellation token, so it wins the race and returns prices anyway - which
        // is what the re-check before the upsert exists to catch. Without that guard, a source the operator
        // just switched off would get a fresh last_updated_utc written for it.
        var source = new BlockingSource("litellm", observeCancellation: false, new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null));
        var service = Build(new FakeRegistry(source), repository, sourceRepository, toggleStore);

        var cycle = service.RunCycleAsync(TestContext.Current.CancellationToken);
        await source.FetchStarted.Task;
        toggleStore.SetEnabled("litellm", enabled: false);
        source.ReleaseFetch();

        var summary = await cycle;

        var outcome = Assert.Single(summary.Outcomes);
        Assert.False(outcome.Succeeded);
        Assert.Equal(PriceCatalogIngestionService.DisabledDuringFetch, outcome.Error);
        Assert.Equal(0, sourceRepository.CountFreshPrices(TimeSpan.FromHours(24)));
    }

    [Fact]
    public async Task RunCycleAsync_SourceDisabledMidFetch_LetsOtherSourcesFinish()
    {
        using var temp = new TempDatabase();
        temp.SeedExtraSource("blocked");
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);

        var blocking = new BlockingSource("blocked", observeCancellation: true, new NormalizedPrice("m1", "p1", 1m, 2m, null, null, null));
        var healthy = new StubSource("litellm", new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null));
        var service = Build(new FakeRegistry(blocking, healthy), repository, sourceRepository, toggleStore);

        var cycle = service.RunCycleAsync(TestContext.Current.CancellationToken);
        await blocking.FetchStarted.Task;
        toggleStore.SetEnabled("blocked", enabled: false);

        var summary = await cycle;

        // A toggle-cancel must not tear down the whole cycle and take every other source's refresh with it -
        // which is exactly what would happen if the catch didn't distinguish it from a host shutdown.
        Assert.Equal(2, summary.Outcomes.Count);
        Assert.Contains(summary.Outcomes, o => o.Source == "blocked" && !o.Succeeded);
        Assert.Contains(summary.Outcomes, o => o.Source == "litellm" && o.Succeeded);
        Assert.Equal(1, summary.FreshPriceCount);
    }

    [Fact]
    public async Task RunCycleAsync_HostShutdown_PropagatesCancellation()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var source = new BlockingSource("litellm", observeCancellation: true, new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null));
        var service = Build(new FakeRegistry(source), repository, sourceRepository, toggleStore);

        using var hostShutdown = new CancellationTokenSource();
        var cycle = service.RunCycleAsync(hostShutdown.Token);
        await source.FetchStarted.Task;
        await hostShutdown.CancelAsync();

        // The caller's token cancelling is the host stopping, not a source failing - it must surface as a
        // cancellation rather than be swallowed into a "source failed" outcome.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
    }

    [Fact]
    public async Task RunCycleAsync_OneSourceFailsAnotherSucceeds_StaysWarningNotError()
    {
        // The rung PriceCatalogIngestionService's own remarks used to call unreachable with one source: a
        // failed cycle no longer means zero fresh prices by construction now that two sources are live.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var registry = new FakeRegistry(
            new StubSource(PriceCatalogOptions.LiteLlmSourceName, new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null)),
            new ThrowingSource(PriceCatalogOptions.OpenRouterSourceName));
        var service = Build(registry, repository, sourceRepository, toggleStore);

        var summary = await service.RunCycleAsync(TestContext.Current.CancellationToken);

        // Not zero: litellm's row is fresh, so the cycle-level Error condition (outcomes.Count > 0 &&
        // freshPriceCount == 0) does not fire - only openrouter's own per-source Warning does.
        Assert.Equal(1, summary.FreshPriceCount);
        Assert.Contains(summary.Outcomes, o => o.Source == PriceCatalogOptions.LiteLlmSourceName && o.Succeeded);
        Assert.Contains(summary.Outcomes, o => o.Source == PriceCatalogOptions.OpenRouterSourceName && !o.Succeeded);
    }

    [Fact]
    public async Task RunCycleAsync_PriorityGateAppliesAcrossARealCycle_HigherRankedSourceWins()
    {
        // End-to-end through the actual seeded ranks (litellm=0 outranks openrouter=-10 by default), not just
        // the repository-level gate test - this is what an operator actually experiences from Pull Now.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var registry = new FakeRegistry(
            new StubSource(PriceCatalogOptions.OpenRouterSourceName, new NormalizedPrice("gpt-4o", "openai", 999m, 999m, null, null, null)),
            new StubSource(PriceCatalogOptions.LiteLlmSourceName, new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null)));
        var service = Build(registry, repository, sourceRepository, toggleStore);

        await service.RunCycleAsync(TestContext.Current.CancellationToken);

        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));
        Assert.Equal(2.5m, price!.InputPerMillionTokens);
    }

    [Fact]
    public async Task RecomputeWinnersAsync_NeverCallsAnySourceFetch()
    {
        // The literal "no live pull" contract: RecomputeWinnersAsync never touches the registry, so a source
        // whose fetch always throws must never get the chance to.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var service = Build(new FakeRegistry(new ExplodingSource("litellm")), repository, sourceRepository, toggleStore);

        await service.RecomputeWinnersAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecomputeWinnersAsync_FlipsContestedCellFromStorage_WithNoFetch()
    {
        using var temp = new TempDatabase();
        temp.SeedExtraSource("high", enabled: true, priorityScore: 10);
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        repository.UpsertPrices("high", 10, [new NormalizedPrice("gpt-4o", "openai", 2.50m, 10.00m, null, null, null)], DateTimeOffset.UtcNow);
        repository.UpsertPrices(PriceCatalogOptions.LiteLlmSourceName, 0, [new NormalizedPrice("gpt-4o", "openai", 999m, 999m, null, null, null)], DateTimeOffset.UtcNow);
        Assert.Equal(2.50m, repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24))!.InputPerMillionTokens);

        Assert.True(toggleStore.Reorder([PriceCatalogOptions.LiteLlmSourceName, "high", PriceCatalogOptions.OpenRouterSourceName]));
        var service = Build(new FakeRegistry(new ExplodingSource("litellm")), repository, sourceRepository, toggleStore);

        var summary = await service.RecomputeWinnersAsync(TestContext.Current.CancellationToken);

        Assert.Empty(summary.Outcomes); // no fetch occurred, so there is nothing to report per-source
        Assert.Equal(999m, repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24))!.InputPerMillionTokens);
    }

    [Fact]
    public async Task RecomputeWinnersAsync_DoesNotReanchorTheSchedule()
    {
        // Only a live pull consumes the poll interval - a recompute reads storage, so the countdown to the
        // next real pull must carry on undisturbed.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var service = Build(new FakeRegistry(), repository, sourceRepository, toggleStore);
        var seeded = service.ScheduleAnchorUtc;
        await Task.Delay(10, TestContext.Current.CancellationToken);

        await service.RecomputeWinnersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(seeded, service.ScheduleAnchorUtc);
    }

    [Fact]
    public async Task RunCycleAsync_ConcurrentCallers_AreSerialized()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);

        // The background poll loop and the panel's Pull Now both land here; two cycles at once would
        // double-fetch every source and race their upserts.
        var source = new BlockingSource("litellm", observeCancellation: true, new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null));
        var service = Build(new FakeRegistry(source), repository, sourceRepository, toggleStore);

        var first = service.RunCycleAsync(TestContext.Current.CancellationToken);
        await source.FetchStarted.Task;

        var second = service.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, source.FetchCount);

        source.ReleaseFetch();
        await first;
        await second;

        Assert.Equal(2, source.FetchCount);
    }

    [Fact]
    public async Task RunCycleAsync_SourceWritesPrices_InvalidatesTheCatalog()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var registry = new FakeRegistry(new StubSource("litellm", new NormalizedPrice("gpt-4o", "openai", 2.5m, 10.0m, null, null, null)));
        var catalog = new RecordingModelPriceCatalog();
        var service = Build(registry, repository, sourceRepository, toggleStore, catalog);

        await service.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, catalog.InvalidateCallCount);
    }

    [Fact]
    public async Task RunCycleAsync_EverySourceFails_DoesNotInvalidateTheCatalog()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var registry = new FakeRegistry(new ThrowingSource("litellm"));
        var catalog = new RecordingModelPriceCatalog();
        var service = Build(registry, repository, sourceRepository, toggleStore, catalog);

        await service.RunCycleAsync(TestContext.Current.CancellationToken);

        // A failed cycle wrote nothing, so the cache serving the last known-good prices must be left alone -
        // see IModelPriceCatalog.Invalidate's own remarks on why eviction is keyed on "a source wrote
        // something" rather than "a cycle ran".
        Assert.Equal(0, catalog.InvalidateCallCount);
    }

    [Fact]
    public async Task RunCycleAsync_NoEnabledSources_DoesNotInvalidateTheCatalog()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        using var toggleStore = temp.CreateToggleStore(sourceRepository);
        var catalog = new RecordingModelPriceCatalog();
        var service = Build(new FakeRegistry(), repository, sourceRepository, toggleStore, catalog);

        await service.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, catalog.InvalidateCallCount);
    }

    private static PriceCatalogIngestionService Build(
        IPriceSourceRegistry registry,
        PriceRepository repository,
        PriceSourceRepository sourceRepository,
        PriceSourceToggleStore toggleStore,
        IModelPriceCatalog? priceCatalog = null) =>
        new(registry, repository, sourceRepository, toggleStore, NullLogger<PriceCatalogIngestionService>.Instance, priceCatalog);

    private sealed class FakeRegistry(params IPriceSourceClient[] clients) : IPriceSourceRegistry
    {
        public IReadOnlyList<IPriceSourceClient> EnabledClients { get; } = clients;
    }

    /// <summary>Spies on <see cref="IModelPriceCatalog.Invalidate"/> calls; the read methods are unused here.</summary>
    private sealed class RecordingModelPriceCatalog : IModelPriceCatalog
    {
        public int InvalidateCallCount { get; private set; }

        public void Invalidate() => InvalidateCallCount++;

        public ModelPrice? GetBestPriceForModel(ModelKey key, PriceContext context) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public ModelPrice? GetFreshPriceForRouting(ModelKey key, PriceContext context, TimeSpan maxAge) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class StubSource(string name, params NormalizedPrice[] prices) : IPriceSourceClient
    {
        public string Name => name;

        public Task<IReadOnlyList<NormalizedPrice>> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NormalizedPrice>>(prices);
    }

    private sealed class ThrowingSource(string name) : IPriceSourceClient
    {
        public string Name => name;

        public Task<IReadOnlyList<NormalizedPrice>> FetchAsync(CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated source outage");
    }

    /// <summary>
    /// Fails the test outright if its fetch is ever invoked - stronger than <see cref="ThrowingSource"/>,
    /// which models an ordinary source failure a cycle is expected to tolerate. Used to prove a code path
    /// makes no live pull at all, not merely that it survives one failing.
    /// </summary>
    private sealed class ExplodingSource(string name) : IPriceSourceClient
    {
        public string Name => name;

        public Task<IReadOnlyList<NormalizedPrice>> FetchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"{name}.FetchAsync was called - this path must never make a live pull.");
    }

    /// <summary>
    /// A source whose fetch blocks until released or cancelled, so a test can act (disable it, cancel the
    /// host) while the fetch is genuinely in flight rather than racing a <c>Task.Delay</c>.
    /// </summary>
    /// <param name="observeCancellation">
    /// When <see langword="true"/> the fetch aborts as soon as its token trips - a well-behaved HTTP client.
    /// When <see langword="false"/> it ignores the token and returns prices anyway, which is how a test
    /// reaches the re-check guard before the upsert rather than the cancellation path.
    /// </param>
    private sealed class BlockingSource(string name, bool observeCancellation, params NormalizedPrice[] prices)
        : IPriceSourceClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => name;

        public TaskCompletionSource FetchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FetchCount { get; private set; }

        public void ReleaseFetch() => _release.TrySetResult();

        public async Task<IReadOnlyList<NormalizedPrice>> FetchAsync(CancellationToken cancellationToken)
        {
            FetchCount++;
            FetchStarted.TrySetResult();

            if (observeCancellation)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            else
            {
                await _release.Task;
            }

            return prices;
        }
    }
}

