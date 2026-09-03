using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// The in-memory working set and retrieval logic over <see cref="IMemoryEntryStore"/>: brute-force
/// cosine kNN with a similarity floor, and FIFO eviction once the working set exceeds a configured
/// capacity - PLAN.md Phase J, research-doc §3.3. Mirrors the <see cref="IRouterMemoryStore"/>/
/// <see cref="RouterMemory"/> split already used for the dimension-hashed memory: the store persists,
/// this class holds the working set and answers retrieval queries.
/// </summary>
/// <remarks>
/// At the 20,000-entry FIFO bound research-doc §3.3 specifies, a brute-force cosine scan over the
/// in-memory working set is well inside the hot-path budget - a vector index is not warranted.
/// <see cref="RoutingOptions.EmbeddingMemoryCapacity"/> is read live from <see cref="IOptionsMonitor{TOptions}"/>
/// rather than captured once (docs/router/self-organizing-classification-plan.md Phase T6): a stored
/// <see cref="RouterSettingsStore"/> override lowering the capacity must trim the working set immediately,
/// not only after a restart, so this class subscribes to <see cref="IOptionsMonitor{TOptions}.OnChange"/> and
/// re-runs the same trim <see cref="InitializeAsync"/> and <see cref="AddEntryAsync"/> already perform.
/// </remarks>
public sealed class EmbeddingMemory : IDisposable
{
    private readonly IMemoryEntryStore _store;
    private readonly IOptionsMonitor<RoutingOptions> _optionsMonitor;
    private readonly Embeddings.IEmbeddingClient _embeddingClient;
    private readonly ILogger<EmbeddingMemory> _logger;
    private readonly object _syncLock = new();
    private readonly List<MemoryEntry> _entries = [];
    private readonly IDisposable? _changeSubscription;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingMemory"/> class.
    /// </summary>
    /// <param name="store">The persistence layer.</param>
    /// <param name="optionsMonitor">The routing options monitor, providing the similarity threshold, neighbor count, and FIFO capacity - read live so a runtime capacity change (Phase T6) takes effect without a restart.</param>
    /// <param name="embeddingClient">
    /// Supplies <see cref="Embeddings.IEmbeddingClient.ModelIdentity"/> - stamped onto every entry written
    /// here and compared against on every retrieval. Only the identity property is read; no inference is
    /// ever run from this class, so taking the dependency costs nothing at construction time.
    /// </param>
    /// <param name="logger">The logger.</param>
    public EmbeddingMemory(
        IMemoryEntryStore store,
        IOptionsMonitor<RoutingOptions> optionsMonitor,
        Embeddings.IEmbeddingClient embeddingClient,
        ILogger<EmbeddingMemory> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(embeddingClient);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _optionsMonitor = optionsMonitor;
        _embeddingClient = embeddingClient;
        _logger = logger;

        // Fire-and-forget on purpose: OnChange's callback is synchronous and must not block the options
        // system's notification dispatch. A caller that needs the trim to have actually completed before
        // it proceeds (e.g. RouterSettingsAdminGrpcService reporting the post-mutation state) calls
        // TrimToCurrentCapacityAsync directly and awaits it instead of relying on this reactive path.
        _changeSubscription = _optionsMonitor.OnChange(OnOptionsChanged);
    }

    /// <summary>
    /// The <see cref="IOptionsMonitor{TOptions}.OnChange"/> callback: fires <see cref="TrimToCurrentCapacityAsync"/>
    /// without awaiting it, since the callback itself is synchronous and must not block the options
    /// system's notification dispatch. A caller that needs the trim to have actually completed before it
    /// proceeds (e.g. <see cref="RouterSettingsAdminGrpcService"/> reporting the post-mutation state) calls
    /// <see cref="TrimToCurrentCapacityAsync"/> directly and awaits it instead of relying on this path.
    /// </summary>
    private void OnOptionsChanged(RoutingOptions changedOptions, string? name) =>
        _ = TrimToCurrentCapacityAsync(CancellationToken.None);

    /// <summary>
    /// Loads the working set from the store. Must be called once before <see cref="FindNearest"/>
    /// or <see cref="AddEntryAsync"/> reflect prior runs. Trims down to
    /// <see cref="RoutingOptions.EmbeddingMemoryCapacity"/> oldest-first if the store holds more than
    /// that (e.g. after a config change lowered the capacity, or a manual import), so the working set
    /// and the store agree with the documented FIFO bound immediately, not only after the next append.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        lock (_syncLock)
        {
            _entries.Clear();
            _entries.AddRange(loaded);
        }

        await TrimToCurrentCapacityAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Initialized embedding memory with {EntryCount} entries.", _entries.Count);
    }

    /// <summary>
    /// Persists a new entry and enforces the FIFO capacity bound, evicting the oldest entries once
    /// <see cref="RoutingOptions.EmbeddingMemoryCapacity"/> is exceeded.
    /// </summary>
    /// <param name="taskEmbedding">The task's unit-normalized embedding vector.</param>
    /// <param name="chosenModel">The model selected for this task.</param>
    /// <param name="score">The Verifier's observed quality score.</param>
    /// <param name="cost">The monetary cost κ of serving this task.</param>
    /// <param name="verifierTrace">The Verifier's trace/explanation, if recorded.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <param name="isExploratory">
    /// Whether the routing decision that chose <paramref name="chosenModel"/> was an epsilon-greedy
    /// exploratory pick (docs/router/self-organizing-classification-plan.md Phase T1c). Defaults to
    /// <see langword="false"/> so every pre-existing caller keeps compiling and behaving unchanged. Placed
    /// after <paramref name="cancellationToken"/>, not before it, so every existing positional call site
    /// (which ends in <paramref name="cancellationToken"/>) keeps compiling unchanged.
    /// </param>
    /// <param name="propensity">
    /// The propensity of <paramref name="chosenModel"/> under the policy's own arm-selection
    /// distribution. Defaults to <c>1.0</c> - certain selection - matching <paramref name="isExploratory"/>'s
    /// default.
    /// </param>
    /// <param name="dimension">
    /// The heuristic classifier's dimension label for this request, or <see langword="null"/> when
    /// unavailable (docs/router/self-organizing-classification-plan.md Phase T2e). Placed after
    /// <paramref name="propensity"/>, not before it, so every existing positional call site keeps
    /// compiling unchanged.
    /// </param>
    /// <param name="isJudgeScored">
    /// Whether <paramref name="score"/> was produced by the G-Eval judge rather than
    /// <see cref="Quality.Scoring.QualityScorer"/> (docs/router/geval-shadow-scoring-plan.md
    /// §Provenance). Defaults to <see langword="false"/> - through Phase G1/G2 no caller ever passes
    /// <see langword="true"/>; the parameter exists so Phase G3 needs no further signature change. Placed
    /// last so every existing positional call site keeps compiling unchanged.
    /// </param>
    public async Task<MemoryEntry> AddEntryAsync(
        float[] taskEmbedding,
        string chosenModel,
        double score,
        double cost,
        string? verifierTrace,
        CancellationToken cancellationToken = default,
        bool isExploratory = false,
        double propensity = 1.0,
        string? dimension = null,
        bool isJudgeScored = false)
    {
        ArgumentNullException.ThrowIfNull(taskEmbedding);
        ArgumentException.ThrowIfNullOrWhiteSpace(chosenModel);

        var persisted = await _store.AppendAsync(
            new MemoryEntry(
                0, taskEmbedding, chosenModel, score, cost, verifierTrace, DateTimeOffset.UtcNow,
                isExploratory, propensity, dimension, isJudgeScored, _embeddingClient.ModelIdentity),
            cancellationToken).ConfigureAwait(false);

        lock (_syncLock)
        {
            _entries.Add(persisted);
        }

        await TrimToCurrentCapacityAsync(cancellationToken).ConfigureAwait(false);

        return persisted;
    }

    /// <summary>
    /// Trims the working set down to <see cref="RoutingOptions.EmbeddingMemoryCapacity"/>'s current value,
    /// oldest-first, deleting evicted rows from the store. A no-op when the working set is already within
    /// bound. Shared by <see cref="InitializeAsync"/>, <see cref="AddEntryAsync"/>, and the
    /// <see cref="IOptionsMonitor{TOptions}.OnChange"/> subscription registered in the constructor, so a
    /// runtime capacity change (docs/router/self-organizing-classification-plan.md Phase T6) enforces the
    /// same bound through the same path as every other trim.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task TrimToCurrentCapacityAsync(CancellationToken cancellationToken = default)
    {
        var capacity = _optionsMonitor.CurrentValue.EmbeddingMemoryCapacity;

        List<MemoryEntry> evicted;
        lock (_syncLock)
        {
            var overflow = _entries.Count - capacity;
            if (overflow <= 0)
            {
                return;
            }

            evicted = _entries.GetRange(0, overflow);
            _entries.RemoveRange(0, overflow);
        }

        foreach (var evictedEntry in evicted)
        {
            await _store.DeleteAsync(evictedEntry.Id, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Trimmed {EvictedCount} oldest embedding memory entries to stay within the {Capacity}-entry FIFO bound.",
            evicted.Count,
            capacity);
    }

    /// <summary>
    /// Finds the top neighbors for a query embedding by cosine similarity, filtered to
    /// <see cref="RoutingOptions.EmbeddingSimilarityThreshold"/> and capped at
    /// <see cref="RoutingOptions.MaxNeighborCount"/>, ordered by descending similarity. Entries whose
    /// stored vector is not comparable to <paramref name="queryEmbedding"/> are excluded before any
    /// similarity is computed - see the remarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why incomparable entries are filtered here rather than left to fail.</b> Before this filter
    /// existed, a change to the embedding model left the FIFO holding vectors of the previous dimension
    /// while fresh queries arrived at the new one; <see cref="CosineSimilarity"/> throws on a length
    /// mismatch, so the first surviving old entry aborted the entire retrieval. Nothing broke visibly -
    /// <c>OrchestratorRoutingPolicy.CastVoteAsync</c> catches every non-cancellation exception and turns
    /// it into an abstention - but <c>memory_kNN</c> then abstained through an exception-and-error-log
    /// path on <em>every</em> request until the stale entries aged out of a 20,000-entry window, which at
    /// ordinary traffic takes a very long time. Skipping them reaches the identical routing outcome by an
    /// honest route: the voter sees no usable neighbors and abstains cleanly, which is the designed answer
    /// to "I have no data" and exactly what <see cref="Orchestrator.LogRegVoter"/> and
    /// <see cref="Orchestrator.ClusterBestVoter"/> already do for their own artifacts.
    /// </para>
    /// <para>
    /// Two independent conditions make an entry incomparable and both are checked, because neither
    /// implies the other: a differing vector length (a dimension change - loud, and what the throw used to
    /// surface) and a differing <see cref="MemoryEntry.EmbeddingModel"/> (a same-dimension model swap -
    /// silent, and undetectable by length alone). <see cref="CosineSimilarity"/> keeps its throw: it is a
    /// genuine programming error for anything to reach it with mismatched lengths, and softening it to a
    /// silent zero would conceal the next such bug rather than surface it.
    /// </para>
    /// </remarks>
    /// <param name="queryEmbedding">The task embedding to find neighbors for.</param>
    public IReadOnlyList<(MemoryEntry Entry, double Similarity)> FindNearest(float[] queryEmbedding)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        var options = _optionsMonitor.CurrentValue;
        var modelIdentity = _embeddingClient.ModelIdentity;

        List<MemoryEntry> snapshot;
        lock (_syncLock)
        {
            snapshot = [.. _entries];
        }

        var comparable = snapshot
            .Where(entry =>
                entry.TaskEmbedding.Length == queryEmbedding.Length &&
                entry.MatchesEmbeddingModel(modelIdentity))
            .ToList();

        // One aggregate line per retrieval, never one per entry: after a model change every entry in the
        // working set is skipped at once, and logging each would recreate the very flood this filter
        // exists to stop.
        if (comparable.Count != snapshot.Count)
        {
            _logger.LogDebug(
                "Skipped {SkippedCount} of {TotalCount} embedding memory entries not comparable to the current embedding model {ModelIdentity}.",
                snapshot.Count - comparable.Count,
                snapshot.Count,
                modelIdentity);
        }

        return comparable
            .Select(entry => (Entry: entry, Similarity: CosineSimilarity(queryEmbedding, entry.TaskEmbedding)))
            .Where(candidate => candidate.Similarity >= options.EmbeddingSimilarityThreshold)
            .OrderByDescending(candidate => candidate.Similarity)
            .Take(options.MaxNeighborCount)
            .ToList();
    }

    /// <summary>
    /// Computes the cosine similarity between two vectors of equal length. Embedding vectors from
    /// <see cref="Embeddings.IEmbeddingClient"/> are already unit-normalized, so this reduces to a plain
    /// dot product for those callers - the division guards any vector that is not.
    /// </summary>
    private static double CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Vectors must be the same length to compute cosine similarity.", nameof(right));
        }

        double dot = 0, leftMagnitude = 0, rightMagnitude = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftMagnitude += (double)left[i] * left[i];
            rightMagnitude += (double)right[i] * right[i];
        }

        if (leftMagnitude <= 0 || rightMagnitude <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    /// <summary>Unsubscribes from <see cref="IOptionsMonitor{TOptions}.OnChange"/>.</summary>
    public void Dispose() => _changeSubscription?.Dispose();
}
