using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Background poll loop that automatically retrains the <c>logreg</c> voter's artifact once
/// <see cref="RoutingOptions.LogRegRetrainThreshold"/> new <c>memory_entries</c> rows have accumulated
/// since the artifact's last recorded <see cref="EmbeddingLogRegModelArtifact.MemoryEntryCount"/>
/// (docs/router/live-feedback-learning-plan.md Phase 4c). Mirrors
/// <see cref="CostReconciliationHostedService"/>'s <see cref="PeriodicTimer"/> shape; always registered,
/// but a no-op every tick while <see cref="RoutingOptions.EnableAutomaticLogRegRetrain"/> is
/// <see langword="false"/> or the threshold has not been crossed.
/// </summary>
public sealed class LogRegRetrainHostedService : BackgroundService
{
    // A local row-count comparison, not a network operation - a short interval costs nothing and keeps an
    // operator from waiting a long poll window after crossing the threshold before a retrain fires.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<LogRegRetrainHostedService> _logger;
    private readonly IEmbeddingLogRegTrainingService _trainingService;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly RoutingOptions _options;
    private readonly string _modelPath;

    /// <summary>Initializes a new instance of the <see cref="LogRegRetrainHostedService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="trainingService">Runs the guarded retrain sequence when the threshold is crossed.</param>
    /// <param name="memoryEntryStore">Supplies the current live memory entry count.</param>
    /// <param name="routingOptions">Provides the retrain threshold and the automatic-retrain enable flag.</param>
    /// <param name="storageOptions">Supplies the model artifact's file path, to read its last recorded entry count.</param>
    public LogRegRetrainHostedService(
        ILogger<LogRegRetrainHostedService> logger,
        IEmbeddingLogRegTrainingService trainingService,
        IMemoryEntryStore memoryEntryStore,
        IOptions<RoutingOptions> routingOptions,
        IOptions<StorageOptions> storageOptions)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(trainingService);
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(routingOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);

        _logger = logger;
        _trainingService = trainingService;
        _memoryEntryStore = memoryEntryStore;
        _options = routingOptions.Value;
        _modelPath = storageOptions.Value.ResolveLogRegModelPath();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableAutomaticLogRegRetrain)
        {
            _logger.LogInformation("Automatic logreg retrain is disabled; this loop will not fire.");
        }

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                try
                {
                    await CheckAndRetrainAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Automatic logreg retrain check threw unexpectedly; continuing.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one threshold check and, if crossed, one retrain - the loop body <see cref="ExecuteAsync"/>
    /// runs on every tick. Internal (not private) so <c>LogRegRetrainHostedServiceTests</c> can exercise
    /// one cycle directly rather than waiting on <see cref="CheckInterval"/>, mirroring
    /// <c>Program.ExtractFlag</c>'s "internal for direct test access" convention.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task CheckAndRetrainAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableAutomaticLogRegRetrain)
        {
            return;
        }

        var lastTrainedCount = TryReadLastTrainedMemoryEntryCount();
        var currentCount = (await _memoryEntryStore.LoadAllAsync(cancellationToken).ConfigureAwait(false)).Count;

        if (currentCount - lastTrainedCount < _options.LogRegRetrainThreshold)
        {
            return;
        }

        _logger.LogInformation(
            "Automatic logreg retrain threshold crossed ({CurrentCount} entries, {LastTrainedCount} at last train, threshold {Threshold}); retraining.",
            currentCount,
            lastTrainedCount,
            _options.LogRegRetrainThreshold);

        var outcome = await _trainingService.RetrainAsync(bootstrapProgress: null, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Automatic logreg retrain finished with outcome {Kind}: {Message}", outcome.Kind, outcome.Message);
    }

    /// <summary>
    /// Reads only the <see cref="EmbeddingLogRegModelArtifact.MemoryEntryCount"/> field of the
    /// artifact currently on disk, tolerating a missing or unreadable file by returning <c>0</c> - the
    /// honest baseline when no artifact has ever been trained.
    /// </summary>
    private int TryReadLastTrainedMemoryEntryCount()
    {
        if (!File.Exists(_modelPath))
        {
            return 0;
        }

        try
        {
            var json = File.ReadAllText(_modelPath);
            return EmbeddingLogRegModelArtifactSerializer.Deserialize(json).MemoryEntryCount;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Failed to read the current logreg artifact's memory entry count from {Path}; assuming 0.", _modelPath);
            return 0;
        }
    }
}
