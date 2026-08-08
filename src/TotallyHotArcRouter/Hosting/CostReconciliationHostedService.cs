using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Background poll loop that runs a <see cref="CostReconciliationService"/> cycle on
/// <see cref="CostReconciliationOptions.PollIntervalHours"/>, mirroring
/// <see cref="PriceCatalogIngestionHostedService"/>'s shape. Always registered (see
/// <c>ServiceCollectionExtensions.AddTotallyHotArcRouter</c>), but reconciliation itself is entirely
/// optional per §5.8: a provider only gets an <see cref="IProviderCostReconciler"/> when its Admin API key
/// is configured, so with none configured the loop still runs on schedule but each cycle is a no-op.
/// </summary>
public sealed class CostReconciliationHostedService : BackgroundService
{
    private readonly ILogger<CostReconciliationHostedService> _logger;
    private readonly CostReconciliationService _reconciliationService;
    private readonly TimeSpan _pollInterval;

    /// <summary>Initializes a new instance of the <see cref="CostReconciliationHostedService"/> class.</summary>
    public CostReconciliationHostedService(
        ILogger<CostReconciliationHostedService> logger,
        CostReconciliationService reconciliationService,
        IOptions<CostReconciliationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(reconciliationService);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _reconciliationService = reconciliationService;
        _pollInterval = TimeSpan.FromHours(options.Value.PollIntervalHours);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Cost reconciliation poll loop starting; interval {IntervalHours}h.", _pollInterval.TotalHours);

        using var timer = new PeriodicTimer(_pollInterval);

        // Runs cycle #1 immediately (unlike PriceCatalogIngestionHostedService, which relies on the startup
        // health check for its first cycle) - reconciliation has no equivalent startup gate, and an
        // operator who just configured an Admin API key expects the first reconciliation to happen soon,
        // not up to a full PollIntervalHours later.
        do
        {
            try
            {
                await _reconciliationService.RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A cycle should already swallow per-provider/per-day failures; this guards the
                // unexpected so a single bad tick never tears down the loop.
                _logger.LogError(ex, "Cost reconciliation cycle threw unexpectedly; continuing.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
