using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// <inheritdoc cref="IClusterTrainingService"/>
/// </summary>
/// <remarks>
/// <b>Never fabricates training data.</b> A missing or unsynced OOD corpus degrades to live-only training
/// (or, with neither source available, a declined retrain) rather than throwing - mirrors
/// <see cref="EmbeddingLogRegTrainingService"/>'s posture exactly.
/// </remarks>
public sealed class ClusterTrainingService : IClusterTrainingService
{
    private readonly OodClusterBootstrapSampleSource _bootstrapSource;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly EmbeddingOptions _embeddingOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<ClusterTrainingService> _logger;
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly string _modelPath;
    private readonly RoutingOptions _routingOptions;
    private readonly ITranscriptStore _transcriptStore;
    private readonly ClusterBestVoter _voter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterTrainingService"/> class.
    /// </summary>
    /// <param name="bootstrapSource">Supplies OOD bootstrap training samples.</param>
    /// <param name="memoryEntryStore">Supplies live <c>memory_entries</c> training samples.</param>
    /// <param name="embeddingClient">
    /// Supplies <see cref="Embeddings.IEmbeddingClient.ModelIdentity"/> - used both to reject live entries
    /// produced by a different embedding model and to stamp the resulting artifact with the identity its
    /// centroids live in. Only the identity property is read; no inference runs here.
    /// </param>
    /// <param name="transcriptStore">Supplies prompt text for top-TF-IDF-term naming, when transcript capture is enabled.</param>
    /// <param name="voter">The <c>cluster_best</c> voter to signal after a successful artifact swap.</param>
    /// <param name="routingOptions">The blend weight, k-sweep range, and degenerate-set threshold.</param>
    /// <param name="embeddingOptions">Supplies the embedding dimension every sample must match.</param>
    /// <param name="storageOptions">Supplies the model artifact's file path.</param>
    /// <param name="logger">The logger.</param>
    public ClusterTrainingService(
        OodClusterBootstrapSampleSource bootstrapSource,
        IMemoryEntryStore memoryEntryStore,
        IEmbeddingClient embeddingClient,
        ITranscriptStore transcriptStore,
        ClusterBestVoter voter,
        IOptions<RoutingOptions> routingOptions,
        IOptions<EmbeddingOptions> embeddingOptions,
        IOptions<StorageOptions> storageOptions,
        ILogger<ClusterTrainingService> logger)
    {
        ArgumentNullException.ThrowIfNull(bootstrapSource);
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(embeddingClient);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(voter);
        ArgumentNullException.ThrowIfNull(routingOptions);
        ArgumentNullException.ThrowIfNull(embeddingOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _bootstrapSource = bootstrapSource;
        _memoryEntryStore = memoryEntryStore;
        _embeddingClient = embeddingClient;
        _transcriptStore = transcriptStore;
        _voter = voter;
        _routingOptions = routingOptions.Value;
        _embeddingOptions = embeddingOptions.Value;
        _modelPath = storageOptions.Value.ResolveClusterModelPath();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ClusterTrainingOutcome> RetrainAsync(
        IProgress<int>? bootstrapProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Cluster model retrain requested while another retrain is already in progress; skipping.");
            return new ClusterTrainingOutcome(Kind: ClusterTrainingResultKind.AlreadyRunning,
                Message: "A retrain was already in progress.", 0, 0, 0, 0);
        }

        try
        {
            return await RetrainCoreAsync(bootstrapProgress: bootstrapProgress, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ClusterTrainingOutcome> RetrainCoreAsync(
        IProgress<int>? bootstrapProgress,
        CancellationToken cancellationToken)
    {
        var dimension = _embeddingOptions.EmbeddingDimension;
        var bootstrapSamples = new List<ClusterTrainingSample>();
        var bootstrapTaskCount = 0;

        try
        {
            bootstrapSamples.AddRange(await _bootstrapSource
                .LoadAsync(progress: bootstrapProgress, cancellationToken: cancellationToken).ConfigureAwait(false));
            bootstrapTaskCount = bootstrapSamples.Count;
        }
        catch (InvalidOperationException ex)
        {
            // Unsynced corpus is an expected, non-broken state (data/README.md) - degrade to live-only
            // training rather than failing the whole retrain.
            _logger.LogInformation(exception: ex,
                message: "Cluster model retrain proceeding without an OOD bootstrap.");
        }

        var modelIdentity = _embeddingClient.ModelIdentity;
        var liveEntries = await _memoryEntryStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var liveSamples = new List<ClusterTrainingSample>();
        var liveEntriesById = new List<MemoryEntry>();
        var memoryEntryCount = 0;
        var skippedForModelMismatch = 0;
        foreach (var entry in liveEntries)
        {
            if (entry.TaskEmbedding.Length != dimension)
            {
                // A dimension change (EmbeddingOptions.EmbeddingDimension or the embedding model URL)
                // invalidates older entries for training purposes - skip rather than let the trainer
                // choke on a ragged embedding length, mirroring EmbeddingLogRegTrainingService's guard.
                _logger.LogWarning(
                    message:
                    "Skipping a memory entry with a {ActualDimension}-dimensional embedding; expected {ExpectedDimension}.",
                    entry.TaskEmbedding.Length,
                    dimension);
                continue;
            }

            if (!entry.MatchesEmbeddingModel(modelIdentity))
            {
                // Same silent hazard EmbeddingLogRegTrainingService guards against, and it bites harder
                // here: centroids averaged across two incomparable coordinate spaces would place every
                // cluster somewhere meaningless, and ClusterBestVoter would then score live requests
                // against them with no outward sign anything was wrong. Counted, reported once below.
                skippedForModelMismatch++;
                continue;
            }

            liveSamples.Add(new ClusterTrainingSample(Embedding: entry.TaskEmbedding, Dimension: entry.Dimension,
                Weight: _routingOptions.ClusterLiveSampleWeight));
            liveEntriesById.Add(entry);
            memoryEntryCount++;
        }

        if (skippedForModelMismatch > 0)
            _logger.LogWarning(
                message:
                "Skipped {SkippedCount} memory entry/entries produced by a different embedding model than the current {ModelIdentity}; they are retained in the store but cannot be clustered.",
                skippedForModelMismatch,
                modelIdentity);

        var samples = new List<ClusterTrainingSample>(bootstrapSamples.Count + liveSamples.Count);
        samples.AddRange(bootstrapSamples);
        samples.AddRange(liveSamples);

        if (samples.Count < _routingOptions.ClusterMinTrainingRows)
        {
            var message =
                $"Declined: {samples.Count} sample(s) - below the configured minimum ({_routingOptions.ClusterMinTrainingRows} rows).";
            _logger.LogWarning(message: "Cluster model retrain {Message}", message);
            return new ClusterTrainingOutcome(
                Kind: ClusterTrainingResultKind.Declined, Message: message, BootstrapTaskCount: bootstrapTaskCount,
                MemoryEntryCount: memoryEntryCount, SampleCount: samples.Count, 0);
        }

        var trainResult = SphericalKMeansTrainer.Train(
            embeddings: [.. samples.Select(s => s.Embedding)],
            weights: [.. samples.Select(s => s.Weight)],
            minK: _routingOptions.ClusterCountMin,
            maxK: _routingOptions.ClusterCountMax);

        var clusterSizes = new int[trainResult.ChosenK];
        var histograms = new Dictionary<string, int>[trainResult.ChosenK];
        for (var c = 0; c < trainResult.ChosenK; c++)
            histograms[c] = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < samples.Count; i++)
        {
            var cluster = trainResult.Assignments[i];
            clusterSizes[cluster]++;

            var sampleDimension = samples[i].Dimension;
            if (sampleDimension is not null)
                histograms[cluster][sampleDimension] = histograms[cluster].GetValueOrDefault(sampleDimension) + 1;
        }

        var topTerms = await ComputeTopTermsAsync(liveEntriesById: liveEntriesById,
                assignments: trainResult.Assignments, bootstrapSampleCount: bootstrapSamples.Count,
                chosenK: trainResult.ChosenK, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var trainedFrom =
            $"bootstrap_tasks={bootstrapTaskCount}, memory_entries={memoryEntryCount}, " +
            $"live_weight={_routingOptions.ClusterLiveSampleWeight:F2}, samples={samples.Count}, " +
            $"trained {DateTimeOffset.UtcNow:O}. {trainResult.KSelectionProvenance}";

        var artifact = new ClusterModelArtifact(
            EmbeddingDimension: dimension,
            Centroids: trainResult.Centroids,
            ChosenK: trainResult.ChosenK,
            TrainedAtUtc: DateTimeOffset.UtcNow,
            ClusterSizes: clusterSizes,
            ClusterDimensionHistograms: [.. histograms.Select(h => (IReadOnlyDictionary<string, int>)h)],
            ClusterTopTerms: topTerms,
            TrainedFrom: trainedFrom,
            BootstrapTaskCount: bootstrapTaskCount,
            MemoryEntryCount: memoryEntryCount,
            EmbeddingModel: modelIdentity);
        ClusterModelArtifactSerializer.Validate(artifact);

        await WriteArtifactAtomicallyAsync(artifact: artifact, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _voter.Reload();

        var trainedMessage =
            $"Trained {trainResult.ChosenK} cluster(s) from {bootstrapTaskCount} bootstrap task(s) and " +
            $"{memoryEntryCount} live memory entry/entries ({samples.Count} sample(s)).";
        _logger.LogInformation(
            message: "Cluster model retrain wrote a new artifact to {Path}: {Message}", _modelPath, trainedMessage);

        return new ClusterTrainingOutcome(
            Kind: ClusterTrainingResultKind.Trained, Message: trainedMessage, BootstrapTaskCount: bootstrapTaskCount,
            MemoryEntryCount: memoryEntryCount, SampleCount: samples.Count, ChosenK: trainResult.ChosenK);
    }

    /// <summary>
    /// Computes each cluster's top TF-IDF-distinguishing terms from live entries' linked transcript
    /// prompt text, when transcript capture is enabled. Returns an empty term list per cluster when it is
    /// not - the artifact still trains and names clusters by dimension histogram alone
    /// (<see cref="ClusterModelArtifact.DescribeCluster"/>), per Phase T2e's documented fallback.
    /// </summary>
    private async Task<IReadOnlyList<IReadOnlyList<string>>> ComputeTopTermsAsync(
        IReadOnlyList<MemoryEntry> liveEntriesById, IReadOnlyList<int> assignments, int bootstrapSampleCount,
        int chosenK, CancellationToken cancellationToken)
    {
        var promptTextByMemoryEntryId = await _transcriptStore.LoadPromptTextByMemoryEntryIdAsync(cancellationToken)
            .ConfigureAwait(false);
        if (promptTextByMemoryEntryId.Count == 0)
            return [.. Enumerable.Repeat(element: (IReadOnlyList<string>)Array.Empty<string>(), count: chosenK)];

        var clusterDocuments = new List<string>[chosenK];
        for (var c = 0; c < chosenK; c++) clusterDocuments[c] = [];

        for (var i = 0; i < liveEntriesById.Count; i++)
        {
            var entry = liveEntriesById[i];
            if (promptTextByMemoryEntryId.TryGetValue(key: entry.Id, value: out var promptText))
            {
                var cluster = assignments[bootstrapSampleCount + i];
                clusterDocuments[cluster].Add(promptText);
            }
        }

        return ClusterTermExtractor.ExtractTopTerms([.. clusterDocuments.Select(d => (IReadOnlyList<string>)d)]);
    }

    /// <summary>
    /// Writes <paramref name="artifact"/> to a temp file alongside <see cref="_modelPath"/> then atomically
    /// renames it into place, so a crash mid-write never leaves a reader a truncated JSON document to fail
    /// on. Mirrors <see cref="EmbeddingLogRegTrainingService.WriteArtifactAtomicallyAsync"/>.
    /// </summary>
    private async Task WriteArtifactAtomicallyAsync(ClusterModelArtifact artifact, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_modelPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var json = ClusterModelArtifactSerializer.Serialize(artifact);
        var tempPath = $"{_modelPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(path: tempPath, contents: json, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        File.Move(sourceFileName: tempPath, destFileName: _modelPath, true);
    }
}