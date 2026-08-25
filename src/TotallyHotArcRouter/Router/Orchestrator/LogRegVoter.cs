using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The <c>logreg</c> voter (docs/router/live-feedback-learning-plan.md Phase 3): scores
/// <see cref="VotingContext.TaskEmbedding"/> against a locally-trained one-vs-rest logistic-regression
/// model (<see cref="EmbeddingLogRegModelArtifact"/>) via a dense dot product, argmax restricted to
/// <see cref="VotingContext.Candidates"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replaces the TF-IDF-over-text design.</b> PLAN.md Phase L's original <c>logreg</c> scored a sparse
/// TF-IDF vector over the task's prompt text against a checked-in placeholder artifact
/// (<see cref="LogRegModelArtifact"/>) - abandoned because CodeRouterBench publishes no task text for the
/// ID splits it was meant to train from (docs/router/live-feedback-learning-plan.md's "What we are
/// actually able to train on"). The embedding is always available once Phase 2 computes one, so this
/// voter scores that instead; <see cref="LogRegModelArtifact"/>/<see cref="LogRegTextTokenizer"/>/
/// <see cref="CodeRouterBench.LogRegTrainer"/> remain in the tree only as Phase N's static comparison
/// baseline (Phase 6 relocates them out of this namespace).
/// </para>
/// <para>
/// <b>No placeholder, ever.</b> The model artifact is per-installation, trained from the operator's own
/// synced corpus and live traffic (Phase 4), and never checked in. With no artifact present - the honest
/// state for a fresh install before any training has run - this voter abstains rather than shipping a
/// fake stand-in, per the plan's "never fabricate training data" ground rule.
/// </para>
/// </remarks>
public sealed class LogRegVoter : IRoutingVoter
{
    private readonly Embeddings.IEmbeddingClient? _embeddingClient;
    private readonly ILogger<LogRegVoter> _logger;
    private readonly string _modelPath;
    private readonly object _loadLock = new();

    private bool _loadAttempted;
    private EmbeddingLogRegModelArtifact? _model;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogRegVoter"/> class, resolving the model artifact's
    /// path from <see cref="StorageOptions.LogRegModelPath"/>. The artifact is loaded lazily on first
    /// vote, not in this constructor - so a missing file never fails DI startup.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="embeddingClient">
    /// Supplies the current <see cref="Embeddings.IEmbeddingClient.ModelIdentity"/>, compared against the
    /// loaded artifact's own so a same-dimension embedding-model swap abstains instead of scoring against
    /// weights fitted in a coordinate space that no longer exists.
    /// </param>
    /// <param name="storageOptions">Supplies the model artifact's file path.</param>
    public LogRegVoter(
        ILogger<LogRegVoter> logger,
        IOptions<StorageOptions> storageOptions,
        Embeddings.IEmbeddingClient embeddingClient)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(embeddingClient);

        _embeddingClient = embeddingClient;
        _logger = logger;
        _modelPath = storageOptions.Value.ResolveLogRegModelPath();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LogRegVoter"/> class with an explicit model artifact -
    /// the seam tests use to exercise this voter against a small, deterministic model without touching
    /// disk.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="model">The model artifact to score against.</param>
    /// <param name="embeddingClient">
    /// The client whose <see cref="Embeddings.IEmbeddingClient.ModelIdentity"/> the artifact's own is
    /// compared against, or <see langword="null"/> to skip that comparison. Optional because this seam
    /// exists for tests that supply an artifact directly and mostly do not care about embedding-model
    /// provenance; a test that does care passes one rather than being forced to construct the disk path.
    /// </param>
    /// <exception cref="FormatException">
    /// <paramref name="model"/> fails <see cref="EmbeddingLogRegModelArtifactSerializer.Validate"/> - e.g.
    /// a wrong-length class-weight vector or a non-finite weight value. Validating here, not just in
    /// <see cref="EmbeddingLogRegModelArtifactSerializer.Deserialize"/>, keeps this constructor's callers -
    /// which may build an <see cref="EmbeddingLogRegModelArtifact"/> directly rather than through
    /// <c>Deserialize</c> - from installing a malformed artifact that would otherwise throw
    /// <see cref="IndexOutOfRangeException"/> later while scoring.
    /// </exception>
    public LogRegVoter(
        ILogger<LogRegVoter> logger,
        EmbeddingLogRegModelArtifact model,
        Embeddings.IEmbeddingClient? embeddingClient = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(model);
        EmbeddingLogRegModelArtifactSerializer.Validate(model);

        _embeddingClient = embeddingClient;
        _logger = logger;
        _modelPath = string.Empty;
        _model = model;
        _loadAttempted = true;
    }

    /// <inheritdoc />
    public string Name => VoterNames.LogReg;

    /// <inheritdoc />
    public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var model = GetModel();
        if (model is null)
        {
            return Task.FromResult(VoterVote.Abstain(Name));
        }

        if (context.TaskEmbedding is null)
        {
            return Task.FromResult(VoterVote.Abstain(Name));
        }

        if (context.TaskEmbedding.Length != model.EmbeddingDimension)
        {
            // A silent index mismatch (scoring a 768-dim embedding against 1024-dim weights) would be far
            // worse than an abstention - see the type-level remarks on why this artifact is never a
            // placeholder someone might trust anyway.
            _logger.LogWarning(
                "logreg voter received a {ActualDimension}-dimensional embedding but its model was trained at {ExpectedDimension}; abstaining.",
                context.TaskEmbedding.Length,
                model.EmbeddingDimension);
            return Task.FromResult(VoterVote.Abstain(Name));
        }

        if (_embeddingClient is not null &&
            model.EmbeddingModel is not null &&
            !string.Equals(model.EmbeddingModel, _embeddingClient.ModelIdentity, StringComparison.Ordinal))
        {
            // The same-dimension model swap the length check above structurally cannot see: the weights
            // were fitted in a coordinate space the current client no longer produces, so scoring against
            // them is arithmetic on unrelated numbers. A null EmbeddingModel is a pre-provenance artifact
            // and is trusted, on the same reasoning MemoryEntry.MatchesEmbeddingModel documents.
            _logger.LogWarning(
                "logreg voter model was trained against embedding model {TrainedModel} but the current client is {CurrentModel}; abstaining until retrained.",
                model.EmbeddingModel,
                _embeddingClient.ModelIdentity);
            return Task.FromResult(VoterVote.Abstain(Name));
        }

        var embedding = context.TaskEmbedding;
        var scored = new List<(string Model, double Score)>();
        foreach (var candidate in context.Candidates)
        {
            // Passing candidate.Provider strips only that candidate's own provider prefix, so a
            // legitimately slashed model id is never mistaken for an unprefixed one and mapped to the
            // wrong class-weight entry - the same convention the TF-IDF predecessor used.
            var key = ModelNameCanonicalizer.Canonicalize(candidate.ModelName, candidate.Provider);
            if (!model.ClassWeights.TryGetValue(key, out var weights))
            {
                continue;
            }

            var score = weights[0]; // bias
            for (var i = 0; i < embedding.Length; i++)
            {
                score += weights[i + 1] * embedding[i];
            }

            scored.Add((candidate.ModelName, score));
        }

        if (scored.Count == 0)
        {
            return Task.FromResult(VoterVote.Abstain(Name));
        }

        // Softmax over the restricted candidate scores turns raw logits into a [0, 1] confidence without
        // changing the argmax - softmax is monotonic in its inputs.
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
        return Task.FromResult(new VoterVote(Name, scored[bestIndex].Model, Math.Clamp(confidence, 0d, 1d)));
    }

    /// <summary>
    /// Forces the next <see cref="VoteAsync"/> call to reload the model artifact from disk, discarding
    /// any cached copy - called after a retrain completes (docs/router/live-feedback-learning-plan.md
    /// Phase 4/5) so a freshly-trained artifact takes effect without restarting the process. A no-op for
    /// an instance constructed with an explicit in-memory artifact (the test seam constructor), since
    /// there is no disk path to reload from.
    /// </summary>
    public void Reload()
    {
        if (string.IsNullOrEmpty(_modelPath))
        {
            return;
        }

        lock (_loadLock)
        {
            _loadAttempted = false;
            _model = null;
        }
    }

    /// <summary>Returns the cached model, attempting a load from disk on the first call (or after a <see cref="Reload"/>).</summary>
    /// <returns>The loaded model artifact, or <see langword="null"/> when none is available.</returns>
    private EmbeddingLogRegModelArtifact? GetModel()
    {
        lock (_loadLock)
        {
            if (_loadAttempted)
            {
                return _model;
            }

            _loadAttempted = true;
            _model = TryLoadFromDisk();
            return _model;
        }
    }

    /// <summary>Reads and deserializes the model artifact from <see cref="_modelPath"/>, tolerating a missing or unreadable file by returning <see langword="null"/> so the voter degrades to an abstention instead of throwing.</summary>
    /// <returns>The deserialized model artifact, or <see langword="null"/> if the file is absent or could not be loaded.</returns>
    private EmbeddingLogRegModelArtifact? TryLoadFromDisk()
    {
        if (!File.Exists(_modelPath))
        {
            _logger.LogDebug("No logreg voter model found at {Path}; voter will abstain until one is trained.", _modelPath);
            return null;
        }

        try
        {
            var json = File.ReadAllText(_modelPath);
            var model = EmbeddingLogRegModelArtifactSerializer.Deserialize(json);
            _logger.LogInformation(
                "Loaded logreg voter model from {Path} (trained from: {TrainedFrom}).", _modelPath, model.TrainedFrom);
            return model;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to load logreg voter model from {Path}; voter will abstain.", _modelPath);
            return null;
        }
    }
}
