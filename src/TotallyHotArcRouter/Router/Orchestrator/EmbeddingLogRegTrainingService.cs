using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// <inheritdoc cref="IEmbeddingLogRegTrainingService"/>
/// </summary>
/// <remarks>
/// <b>Never fabricates training data.</b> A missing or unsynced OOD corpus degrades to live-only
/// training (or, with neither source available, a declined retrain) rather than throwing - the plan's
/// "never fabricate training data" ground rule applies equally to "never fail routing because a training
/// input is temporarily unavailable."
/// </remarks>
public sealed class EmbeddingLogRegTrainingService : IEmbeddingLogRegTrainingService
{
    private readonly OodBootstrapSampleSource _bootstrapSource;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly LogRegVoter _voter;
    private readonly RoutingOptions _routingOptions;
    private readonly EmbeddingOptions _embeddingOptions;
    private readonly string _modelPath;
    private readonly ILogger<EmbeddingLogRegTrainingService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingLogRegTrainingService"/> class.
    /// </summary>
    /// <param name="bootstrapSource">Supplies OOD bootstrap training samples.</param>
    /// <param name="memoryEntryStore">Supplies live <c>memory_entries</c> training samples.</param>
    /// <param name="voter">The <c>logreg</c> voter to signal after a successful artifact swap.</param>
    /// <param name="routingOptions">The blend weight and degenerate-set thresholds.</param>
    /// <param name="embeddingOptions">Supplies the embedding dimension every sample must match.</param>
    /// <param name="storageOptions">Supplies the model artifact's file path.</param>
    /// <param name="logger">The logger.</param>
    public EmbeddingLogRegTrainingService(
        OodBootstrapSampleSource bootstrapSource,
        IMemoryEntryStore memoryEntryStore,
        LogRegVoter voter,
        IOptions<RoutingOptions> routingOptions,
        IOptions<EmbeddingOptions> embeddingOptions,
        IOptions<StorageOptions> storageOptions,
        ILogger<EmbeddingLogRegTrainingService> logger)
    {
        ArgumentNullException.ThrowIfNull(bootstrapSource);
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(voter);
        ArgumentNullException.ThrowIfNull(routingOptions);
        ArgumentNullException.ThrowIfNull(embeddingOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _bootstrapSource = bootstrapSource;
        _memoryEntryStore = memoryEntryStore;
        _voter = voter;
        _routingOptions = routingOptions.Value;
        _embeddingOptions = embeddingOptions.Value;
        _modelPath = storageOptions.Value.ResolveLogRegModelPath();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LogRegTrainingOutcome> RetrainAsync(
        IProgress<int>? bootstrapProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("logreg retrain requested while another retrain is already in progress; skipping.");
            return new LogRegTrainingOutcome(LogRegTrainingResultKind.AlreadyRunning,
                "A retrain was already in progress.", 0, 0, 0, 0);
        }

        try
        {
            return await RetrainCoreAsync(bootstrapProgress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LogRegTrainingOutcome> RetrainCoreAsync(
        IProgress<int>? bootstrapProgress,
        CancellationToken cancellationToken)
    {
        var dimension = _embeddingOptions.EmbeddingDimension;
        var samples = new List<LogRegTrainingSample>();
        var bootstrapTaskCount = 0;

        try
        {
            var (bootstrapSamples, taskCount) = await _bootstrapSource
                .LoadAsync(bootstrapProgress, cancellationToken).ConfigureAwait(false);
            samples.AddRange(bootstrapSamples);
            bootstrapTaskCount = taskCount;
        }
        catch (InvalidOperationException ex)
        {
            // Unsynced corpus is an expected, non-broken state (data/README.md) - degrade to live-only
            // training rather than failing the whole retrain.
            _logger.LogInformation(ex, "logreg retrain proceeding without an OOD bootstrap.");
        }

        var liveEntries = await _memoryEntryStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var memoryEntryCount = 0;
        foreach (var entry in liveEntries)
        {
            if (entry.TaskEmbedding.Length != dimension)
            {
                // A dimension change (EmbeddingOptions.EmbeddingDimension or the embedding model URL)
                // invalidates older entries for training purposes - skip rather than let the trainer
                // choke on a ragged embedding length. LogRegVoter applies the same guard at scoring time.
                _logger.LogWarning(
                    "Skipping a memory entry with a {ActualDimension}-dimensional embedding; expected {ExpectedDimension}.",
                    entry.TaskEmbedding.Length,
                    dimension);
                continue;
            }

            samples.Add(new LogRegTrainingSample(
                entry.TaskEmbedding,
                ModelNameCanonicalizer.Canonicalize(entry.ChosenModel),
                entry.Score,
                _routingOptions.LogRegLiveSampleWeight));
            memoryEntryCount++;
        }

        var modelsRepresented = samples.Select(s => s.ModelKey).Distinct(StringComparer.Ordinal).Count();

        if (samples.Count < _routingOptions.LogRegMinTrainingRows || modelsRepresented < _routingOptions.LogRegMinModelsRepresented)
        {
            var message =
                $"Declined: {samples.Count} sample(s) across {modelsRepresented} model(s) - " +
                $"below the configured minimum ({_routingOptions.LogRegMinTrainingRows} rows, {_routingOptions.LogRegMinModelsRepresented} models).";
            _logger.LogWarning("logreg retrain {Message}", message);
            return new LogRegTrainingOutcome(
                LogRegTrainingResultKind.Declined, message, bootstrapTaskCount, memoryEntryCount, samples.Count, modelsRepresented);
        }

        var trainedFrom =
            $"bootstrap_tasks={bootstrapTaskCount}, memory_entries={memoryEntryCount}, " +
            $"live_weight={_routingOptions.LogRegLiveSampleWeight:F2}, samples={samples.Count}, " +
            $"models={modelsRepresented}, trained {DateTimeOffset.UtcNow:O}";

        var artifact = EmbeddingLogRegTrainer.Train(
            samples, dimension, trainedFrom, bootstrapTaskCount, memoryEntryCount);
        EmbeddingLogRegModelArtifactSerializer.Validate(artifact);

        await WriteArtifactAtomicallyAsync(artifact, cancellationToken).ConfigureAwait(false);
        _voter.Reload();

        var trainedMessage =
            $"Trained from {bootstrapTaskCount} bootstrap task(s) and {memoryEntryCount} live memory entry/entries " +
            $"({samples.Count} sample(s), {modelsRepresented} model(s)).";
        _logger.LogInformation(
            "logreg retrain wrote a new artifact to {Path}: {Message}", _modelPath, trainedMessage);

        return new LogRegTrainingOutcome(
            LogRegTrainingResultKind.Trained, trainedMessage, bootstrapTaskCount, memoryEntryCount, samples.Count, modelsRepresented);
    }

    /// <summary>
    /// Writes <paramref name="artifact"/> to a temp file alongside <see cref="_modelPath"/> then
    /// atomically renames it into place, so a crash mid-write never leaves <see cref="LogRegVoter"/> a
    /// truncated JSON document to fail on.
    /// </summary>
    private async Task WriteArtifactAtomicallyAsync(EmbeddingLogRegModelArtifact artifact, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_modelPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = EmbeddingLogRegModelArtifactSerializer.Serialize(artifact);
        var tempPath = $"{_modelPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _modelPath, overwrite: true);
    }
}
