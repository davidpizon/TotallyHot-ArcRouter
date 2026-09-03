using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Background poll loop that automatically retrains the self-organizing cluster model's artifact once
/// <see cref="RoutingOptions.ClusterRetrainThreshold"/> new <c>memory_entries</c> rows have accumulated
/// since the artifact's last recorded <see cref="ClusterModelArtifact.MemoryEntryCount"/>
/// (docs/router/self-organizing-classification-plan.md Phase T2g). Mirrors
/// <see cref="LogRegRetrainHostedService"/>'s <see cref="PeriodicTimer"/> shape exactly; always
/// registered, but a no-op every tick while <see cref="RoutingOptions.EnableAutomaticClusterRetrain"/> is
/// <see langword="false"/> or the threshold has not been crossed.
/// </summary>
public sealed class ClusterRetrainHostedService : BackgroundService
{
    // A local row-count comparison, not a network operation - a short interval costs nothing and keeps an
    // operator from waiting a long poll window after crossing the threshold before a retrain fires.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<ClusterRetrainHostedService> _logger;
    private readonly IClusterTrainingService _trainingService;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly IOptionsMonitor<RoutingOptions> _optionsMonitor;
    private readonly string _modelPath;

    /// <summary>
    /// The current routing options, read live on every poll cycle rather than captured once - so
    /// <see cref="RoutingOptions.EnableAdaptiveRouting"/> and <see cref="RoutingOptions.EnableAutomaticClusterRetrain"/>
    /// (docs/router/self-organizing-classification-plan.md Phase T6) take effect on the very next tick,
    /// without a restart.
    /// </summary>
    private RoutingOptions Options => _optionsMonitor.CurrentValue;

    /// <summary>Initializes a new instance of the <see cref="ClusterRetrainHostedService"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="trainingService">Runs the guarded retrain sequence when the threshold is crossed.</param>
    /// <param name="memoryEntryStore">Supplies the current live memory entry count.</param>
    /// <param name="routingOptionsMonitor">Provides the retrain threshold and the automatic-retrain/adaptive-routing enable flags, read live.</param>
    /// <param name="storageOptions">Supplies the model artifact's file path, to read its last recorded entry count.</param>
    public ClusterRetrainHostedService(
        ILogger<ClusterRetrainHostedService> logger,
        IClusterTrainingService trainingService,
        IMemoryEntryStore memoryEntryStore,
        IOptionsMonitor<RoutingOptions> routingOptionsMonitor,
        IOptions<StorageOptions> storageOptions)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(trainingService);
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(routingOptionsMonitor);
        ArgumentNullException.ThrowIfNull(storageOptions);

        _logger = logger;
        _trainingService = trainingService;
        _memoryEntryStore = memoryEntryStore;
        _optionsMonitor = routingOptionsMonitor;
        _modelPath = storageOptions.Value.ResolveClusterModelPath();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.EnableAutomaticClusterRetrain)
        {
            _logger.LogInformation("Automatic cluster model retrain is disabled; this loop will not fire.");
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
                    _logger.LogError(ex, "Automatic cluster model retrain check threw unexpectedly; continuing.");
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
    /// runs on every tick. Internal (not private) so tests can exercise one cycle directly rather than
    /// waiting on <see cref="CheckInterval"/>, mirroring <see cref="LogRegRetrainHostedService.CheckAndRetrainAsync"/>'s
    /// convention.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task CheckAndRetrainAsync(CancellationToken cancellationToken)
    {
        // docs/router/self-organizing-classification-plan.md Phase T6: automatic cluster retraining now
        // also requires the adaptive-routing master switch, on top of its own EnableAutomaticClusterRetrain
        // flag.
        if (!Options.EnableAutomaticClusterRetrain || !Options.EnableAdaptiveRouting)
        {
            return;
        }

        var lastTrainedCount = TryReadLastTrainedMemoryEntryCount();
        var currentCount = (await _memoryEntryStore.LoadAllAsync(cancellationToken).ConfigureAwait(false)).Count;

        if (currentCount - lastTrainedCount < Options.ClusterRetrainThreshold)
        {
            return;
        }

        _logger.LogInformation(
            "Automatic cluster model retrain threshold crossed ({CurrentCount} entries, {LastTrainedCount} at last train, threshold {Threshold}); retraining.",
            currentCount,
            lastTrainedCount,
            Options.ClusterRetrainThreshold);

        var outcome = await _trainingService.RetrainAsync(bootstrapProgress: null, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Automatic cluster model retrain finished with outcome {Kind}: {Message}", outcome.Kind, outcome.Message);
    }

    /// <summary>
    /// Reads only the <see cref="ClusterModelArtifact.MemoryEntryCount"/> field of the artifact currently
    /// on disk, tolerating a missing or unreadable file by returning <c>0</c> - the honest baseline when
    /// no artifact has ever been trained.
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
            return ClusterModelArtifactSerializer.Deserialize(json).MemoryEntryCount;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Failed to read the current cluster model artifact's memory entry count from {Path}; assuming 0.", _modelPath);
            return 0;
        }
    }
}
