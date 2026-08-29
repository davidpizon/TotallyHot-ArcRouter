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
    private readonly AnthropicUsageReportService? _usageReportService;
    private readonly TimeSpan _pollInterval;

    /// <summary>Initializes a new instance of the <see cref="CostReconciliationHostedService"/> class.</summary>
    /// <param name="logger">Logs pool-loop start and any unexpected per-cycle failure.</param>
    /// <param name="reconciliationService">Runs one cost-reconciliation cycle per tick.</param>
    /// <param name="options">Provides <see cref="CostReconciliationOptions.PollIntervalHours"/>.</param>
    /// <param name="usageReportService">
    /// Optional. Refreshes Anthropic's reported per-model daily token usage
    /// (docs/router/secrets-at-rest-plan.md §8.1) on the same cycle as reconciliation, right after it. Runs
    /// on every tick when supplied - it swallows its own no-key/failure cases internally - so a
    /// <see langword="null"/> caller (only tests today) simply skips this step.
    /// </param>
    public CostReconciliationHostedService(
        ILogger<CostReconciliationHostedService> logger,
        CostReconciliationService reconciliationService,
        IOptions<CostReconciliationOptions> options,
        AnthropicUsageReportService? usageReportService = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(reconciliationService);
        ArgumentNullException.ThrowIfNull(options);
        options.Value.EnsureValid();

        _logger = logger;
        _reconciliationService = reconciliationService;
        _usageReportService = usageReportService;
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
        try
        {
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

                if (_usageReportService is not null)
                {
                    // A separate try/catch: a reconciliation failure above must not skip the usage-report
                    // refresh, and vice versa - the two are independent optional features sharing one timer.
                    try
                    {
                        await _usageReportService.RunCycleAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Anthropic usage report refresh threw unexpectedly; continuing.");
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
