using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The <c>cluster_best</c> voter (docs/router/self-organizing-classification-plan.md Phase T3): assigns
/// <see cref="VotingContext.TaskEmbedding"/> to its nearest self-organizing cluster
/// (<see cref="ClusterModelArtifact"/>, <see cref="ClusterLedger.AssignNearestCluster"/>) and votes for the
/// candidate with the highest observed mean score in that cluster's ledger, restricted to models meeting
/// <see cref="RoutingOptions.ClusterBestMinObservations"/>.
/// </summary>
/// <remarks>
/// <para>
/// A fifth vote alongside <c>dim_best</c>, <c>memory_kNN</c>, <c>logreg</c>, and <c>llm_router</c> - additive,
/// not a replacement for the fixed nine-dimension vocabulary those voters key on (the plan's "Decision: the
/// learned taxonomy is additive, not a replacement").
/// </para>
/// <para>
/// <b>No placeholder, ever.</b> The cluster model artifact is per-installation, trained from the operator's
/// own live traffic and OOD bootstrap (Phase T2), and never checked in. With no artifact present - the
/// honest state for a fresh install before any training has run - this voter abstains rather than shipping a
/// fake stand-in, per the plan's "never fabricate training data" ground rule. It also abstains when the
/// embedding is unclustered (below <see cref="RoutingOptions.ClusterAssignmentThreshold"/>) or when every
/// candidate's ledger cell falls short of <see cref="RoutingOptions.ClusterBestMinObservations"/>.
/// </para>
/// </remarks>
public sealed class ClusterBestVoter : IRoutingVoter
{
    private readonly IMemoryEntryStore _memoryEntryStore;
    private readonly RoutingOptions _routingOptions;
    private readonly ILogger<ClusterBestVoter> _logger;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private bool _loadAttempted;
    private ClusterModelArtifact? _artifact;
    private IReadOnlyDictionary<int, IReadOnlyDictionary<string, ClusterLedger.ClusterModelScore>>? _ledger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterBestVoter"/> class, resolving the model
    /// artifact's path from <see cref="StorageOptions.ClusterModelPath"/>. The artifact and its ledger are
    /// loaded lazily on first vote, not in this constructor - so a missing file never fails DI startup.
    /// </summary>
    /// <param name="memoryEntryStore">Supplies the live <c>memory_entries</c> working set the ledger is recomputed from.</param>
    /// <param name="routingOptions">The assignment threshold and per-cell observation floor.</param>
    /// <param name="storageOptions">Supplies the model artifact's file path.</param>
    /// <param name="logger">The logger.</param>
    public ClusterBestVoter(
        IMemoryEntryStore memoryEntryStore,
        IOptions<RoutingOptions> routingOptions,
        IOptions<StorageOptions> storageOptions,
        ILogger<ClusterBestVoter> logger)
    {
        ArgumentNullException.ThrowIfNull(memoryEntryStore);
        ArgumentNullException.ThrowIfNull(routingOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _memoryEntryStore = memoryEntryStore;
        _routingOptions = routingOptions.Value;
        _logger = logger;
        _modelPath = storageOptions.Value.ResolveClusterModelPath();
    }

    /// <inheritdoc />
    public string Name => VoterNames.ClusterBest;

    /// <inheritdoc />
    public async Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TaskEmbedding is null)
        {
            return VoterVote.Abstain(Name);
        }

        var (artifact, ledger) = await GetModelAsync(cancellationToken).ConfigureAwait(false);
        if (artifact is null || ledger is null)
        {
            return VoterVote.Abstain(Name);
        }

        if (context.TaskEmbedding.Length != artifact.EmbeddingDimension)
        {
            // A silent index mismatch would score against the wrong centroid space entirely - see the
            // type-level remarks on why this artifact is never a placeholder someone might trust anyway.
            _logger.LogWarning(
                "cluster_best voter received a {ActualDimension}-dimensional embedding but its model was trained at {ExpectedDimension}; abstaining.",
                context.TaskEmbedding.Length,
                artifact.EmbeddingDimension);
            return VoterVote.Abstain(Name);
        }

        var (clusterIndex, similarity) = ClusterLedger.AssignNearestCluster(artifact, context.TaskEmbedding);
        if (similarity < _routingOptions.ClusterAssignmentThreshold)
        {
            // Unclustered - a designed abstention, not a forced low-confidence guess (Phase T3's exit bar).
            return VoterVote.Abstain(Name);
        }

        if (!ledger.TryGetValue(clusterIndex, out var clusterScores))
        {
            return VoterVote.Abstain(Name);
        }

        var scored = new List<(string Model, double Score)>();
        foreach (var candidate in context.Candidates)
        {
            var key = ModelNameCanonicalizer.Canonicalize(candidate.ModelName, candidate.Provider);
            if (!clusterScores.TryGetValue(key, out var cell) || cell.ObservationCount < _routingOptions.ClusterBestMinObservations)
            {
                continue;
            }

            scored.Add((candidate.ModelName, cell.MeanScore));
        }

        if (scored.Count == 0)
        {
            return VoterVote.Abstain(Name);
        }

        // Softmax over the restricted candidate scores turns raw mean scores into a [0, 1] confidence
        // without changing the argmax - softmax is monotonic in its inputs. Mirrors LogRegVoter.VoteAsync.
        var max = scored.Max(s => s.Score);
        var expScores = scored.Select(s => Math.Exp(s.Score - max)).ToList();
        var sumExp = expScores.Sum();

        var bestIndex = 0;
        for (var i = 1; i < scored.Count; i++)
        {
            if (expScores[i] > expScores[bestIndex])
            {
                bestIndex = i;
            }
        }

        var confidence = sumExp > 0 ? expScores[bestIndex] / sumExp : 1d / scored.Count;
        return new VoterVote(Name, scored[bestIndex].Model, Math.Clamp(confidence, 0d, 1d));
    }

    /// <summary>
    /// Forces the next <see cref="VoteAsync"/> call to reload the model artifact and recompute its ledger
    /// from disk/live memory, discarding any cached copy - called after a retrain completes
    /// (<see cref="ClusterTrainingService"/>, Phase T2f's "ledger-as-view") so a freshly-trained artifact
    /// takes effect without restarting the process.
    /// </summary>
    public void Reload()
    {
        _loadLock.Wait();
        try
        {
            _loadAttempted = false;
            _artifact = null;
            _ledger = null;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Returns the cached artifact and its freshly-assembled ledger, attempting a load from disk (and a
    /// ledger rebuild from the current live memory working set) on the first call or after a
    /// <see cref="Reload"/>.
    /// </summary>
    private async Task<(ClusterModelArtifact? Artifact, IReadOnlyDictionary<int, IReadOnlyDictionary<string, ClusterLedger.ClusterModelScore>>? Ledger)> GetModelAsync(
        CancellationToken cancellationToken)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loadAttempted)
            {
                return (_artifact, _ledger);
            }

            _loadAttempted = true;
            _artifact = ClusterModelArtifactLoader.TryLoad(_modelPath, _logger, VoterNames.ClusterBest);
            if (_artifact is not null)
            {
                var entries = await _memoryEntryStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
                _ledger = ClusterLedger.Build(_artifact, entries, _routingOptions.ClusterAssignmentThreshold);
            }

            return (_artifact, _ledger);
        }
        finally
        {
            _loadLock.Release();
        }
    }

}
