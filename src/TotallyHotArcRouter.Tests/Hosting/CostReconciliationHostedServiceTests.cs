using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="CostReconciliationHostedService"/>'s poll loop: unlike
/// <see cref="PriceCatalogIngestionHostedService"/>, it runs cycle #1 immediately rather than waiting for
/// the startup health check, and it is always registered even with reconciliation entirely unconfigured.
/// </summary>
public class CostReconciliationHostedServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CostReconciliationService BuildReconciliationService(
        TempDatabase temp, IEnumerable<IProviderCostReconciler> reconcilers)
    {
        return new CostReconciliationService(
            reconcilers: reconcilers,
            usageLedger: temp.CreateUsageLedger(),
            store: temp.CreateCostReconciliationStore(),
            options: Options.Create(new CostReconciliationOptions()),
            logger: NullLogger<CostReconciliationService>.Instance);
    }

    [Fact]
    public void Constructor_RejectsNonPositivePollInterval()
    {
        using var temp = new TempDatabase();
        var reconciliationService = BuildReconciliationService(temp: temp, reconcilers: []);

        Assert.Throws<OptionsValidationException>(() => new CostReconciliationHostedService(
            logger: NullLogger<CostReconciliationHostedService>.Instance,
            reconciliationService: reconciliationService,
            options: Options.Create(new CostReconciliationOptions { PollIntervalHours = 0 })));
    }

    [Fact]
    public async Task ExecuteAsync_RunsTheFirstCycleImmediately_WithoutWaitingForThePollInterval()
    {
        using var temp = new TempDatabase();
        var reconciler = new RecordingReconciler("openai");
        var reconciliationService = BuildReconciliationService(temp: temp, reconcilers: [reconciler]);
        // A 1-hour interval (the minimum EnsureValid allows) would never tick again inside a test's
        // lifetime; the assertion here only depends on the immediate first cycle, not a second tick.
        var service = new CostReconciliationHostedService(
            logger: NullLogger<CostReconciliationHostedService>.Instance,
            reconciliationService: reconciliationService,
            options: Options.Create(new CostReconciliationOptions { PollIntervalHours = 1 }));

        await service.StartAsync(Ct);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (reconciler.CallCount == 0 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(5, Ct);

            Assert.True(reconciler.CallCount > 0);
        }
        finally
        {
            await service.StopAsync(Ct);
            service.Dispose();
        }
    }

    [Fact]
    public async Task StopAsync_CancelsThePollLoopPromptly()
    {
        using var temp = new TempDatabase();
        var reconciliationService = BuildReconciliationService(temp: temp, reconcilers: []);
        var service = new CostReconciliationHostedService(
            logger: NullLogger<CostReconciliationHostedService>.Instance,
            reconciliationService: reconciliationService,
            options: Options.Create(new CostReconciliationOptions { PollIntervalHours = 1 }));

        await service.StartAsync(Ct);
        await Task.Delay(20, Ct);

        var stopTask = service.StopAsync(Ct);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5), Ct));

        Assert.Same(stopTask, completed);
        service.Dispose();
    }

    private sealed class RecordingReconciler(string provider) : IProviderCostReconciler
    {
        public int CallCount { get; private set; }

        public string Provider => provider;

        public Task<decimal> GetReportedCostAsync(DateOnly day, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(0m);
        }
    }
}
