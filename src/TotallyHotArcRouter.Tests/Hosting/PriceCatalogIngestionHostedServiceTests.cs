using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="PriceCatalogIngestionHostedService"/>'s poll loop shape: it does not run a cycle
/// before it is actually due, and it unwinds cleanly on <see cref="PriceCatalogIngestionHostedService.StopAsync"/>
/// whether or not it ever started.
/// </summary>
/// <remarks>
/// Deliberately does not exercise the "already due" branch that calls <see cref="PriceCatalogIngestionService.RunCycleAsync"/>
/// from the loop: the only way to make that due within a test's lifetime is a zero- or negative-hour poll
/// interval (production only ever configures 4-12h, per <see cref="PriceCatalogOptions"/>), and a zero
/// interval makes the loop re-check <em>immediately</em> after every cycle with no delay at all - confirmed
/// experimentally to busy-spin fast enough to starve the thread pool and hang the test process rather than
/// merely run slowly. That branch is exercised indirectly by <c>StartupHealthCheckHostedServiceTests</c>
/// and the ingestion service's own tests calling <c>RunCycleAsync</c> directly.
/// </remarks>
public class PriceCatalogIngestionHostedServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PriceCatalogIngestionService BuildIngestionService(TempDatabase temp)
    {
        var sourceRepository = temp.CreateSourceRepository();
        var registry = Mock.Of<IPriceSourceRegistry>(r => r.EnabledClients == Array.Empty<IPriceSourceClient>());
        return new PriceCatalogIngestionService(
            registry: registry,
            repository: temp.CreateRepository(),
            sourceRepository: sourceRepository,
            toggleStore: temp.CreateToggleStore(sourceRepository),
            logger: NullLogger<PriceCatalogIngestionService>.Instance);
    }

    private static PriceCatalogIngestionHostedService BuildHostedService(
        PriceCatalogIngestionService ingestionService, int pollIntervalHours = 6)
    {
        return new PriceCatalogIngestionHostedService(
            logger: NullLogger<PriceCatalogIngestionHostedService>.Instance,
            ingestionService: ingestionService,
            options: Options.Create(new PriceCatalogOptions { PollIntervalHours = pollIntervalHours }));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        using var temp = new TempDatabase();
        var ingestionService = BuildIngestionService(temp);

        Assert.Throws<ArgumentNullException>(() => new PriceCatalogIngestionHostedService(
            logger: null!,
            ingestionService: ingestionService,
            options: Options.Create(new PriceCatalogOptions())));
    }

    [Fact]
    public void Constructor_NullIngestionService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PriceCatalogIngestionHostedService(
            logger: NullLogger<PriceCatalogIngestionHostedService>.Instance,
            ingestionService: null!,
            options: Options.Create(new PriceCatalogOptions())));
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        using var temp = new TempDatabase();
        var ingestionService = BuildIngestionService(temp);

        Assert.Throws<ArgumentNullException>(() => new PriceCatalogIngestionHostedService(
            logger: NullLogger<PriceCatalogIngestionHostedService>.Instance,
            ingestionService: ingestionService,
            options: null!));
    }

    [Fact]
    public async Task StartAsync_WhenNotYetDue_DoesNotRunACycleBeforeStop()
    {
        using var temp = new TempDatabase();
        // A 12-hour interval anchored at construction is never due inside a test's lifetime, so the loop
        // stays parked in its Task.Delay - the anchor must not move just because the host started.
        var ingestionService = BuildIngestionService(temp);
        var initialAnchor = ingestionService.ScheduleAnchorUtc;
        var service = BuildHostedService(ingestionService: ingestionService, pollIntervalHours: 12);

        await service.StartAsync(Ct);
        await Task.Delay(20, Ct);
        await service.StopAsync(Ct);
        service.Dispose();

        Assert.Equal(expected: initialAnchor, actual: ingestionService.ScheduleAnchorUtc);
    }

    [Fact]
    public async Task StopAsync_WithoutHavingStarted_CompletesWithoutError()
    {
        using var temp = new TempDatabase();
        var service = BuildHostedService(BuildIngestionService(temp));

        await service.StopAsync(Ct);
        service.Dispose();
    }

    [Fact]
    public async Task StartAsync_ThenStop_DisposeDoesNotThrow()
    {
        using var temp = new TempDatabase();
        var service = BuildHostedService(BuildIngestionService(temp));

        await service.StartAsync(Ct);
        await service.StopAsync(Ct);

        service.Dispose();
    }
}
