using Microsoft.Extensions.Logging;
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
/// </remarks>
public sealed class EmbeddingMemory
{
    private readonly IMemoryEntryStore _store;
    private readonly RoutingOptions _options;
    private readonly ILogger<EmbeddingMemory> _logger;
    private readonly object _syncLock = new();
    private readonly List<MemoryEntry> _entries = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingMemory"/> class.
    /// </summary>
    /// <param name="store">The persistence layer.</param>
    /// <param name="options">The routing options, providing the similarity threshold, neighbor count, and FIFO capacity.</param>
    /// <param name="logger">The logger.</param>
    public EmbeddingMemory(IMemoryEntryStore store, IOptions<RoutingOptions> options, ILogger<EmbeddingMemory> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _options = options.Value;
        _logger = logger;
    }

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

        List<MemoryEntry> evicted = [];
        lock (_syncLock)
        {
            _entries.Clear();
            _entries.AddRange(loaded);

            var overflow = _entries.Count - _options.EmbeddingMemoryCapacity;
            if (overflow > 0)
            {
                evicted = _entries.GetRange(0, overflow);
                _entries.RemoveRange(0, overflow);
            }
        }

        foreach (var evictedEntry in evicted)
        {
            await _store.DeleteAsync(evictedEntry.Id, cancellationToken).ConfigureAwait(false);
        }

        if (evicted.Count > 0)
        {
            _logger.LogInformation(
                "Trimmed {EvictedCount} oldest embedding memory entries on load to stay within the {Capacity}-entry FIFO bound.",
                evicted.Count,
                _options.EmbeddingMemoryCapacity);
        }

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
    public async Task<MemoryEntry> AddEntryAsync(
        float[] taskEmbedding,
        string chosenModel,
        double score,
        double cost,
        string? verifierTrace,
        CancellationToken cancellationToken = default,
        bool isExploratory = false,
        double propensity = 1.0)
    {
        ArgumentNullException.ThrowIfNull(taskEmbedding);
        ArgumentException.ThrowIfNullOrWhiteSpace(chosenModel);

        var persisted = await _store.AppendAsync(
            new MemoryEntry(0, taskEmbedding, chosenModel, score, cost, verifierTrace, DateTimeOffset.UtcNow, isExploratory, propensity),
            cancellationToken).ConfigureAwait(false);

        List<MemoryEntry>? evicted = null;
        lock (_syncLock)
        {
            _entries.Add(persisted);

            var overflow = _entries.Count - _options.EmbeddingMemoryCapacity;
            if (overflow > 0)
            {
                evicted = _entries.GetRange(0, overflow);
                _entries.RemoveRange(0, overflow);
            }
        }

        if (evicted is not null)
        {
            foreach (var evictedEntry in evicted)
            {
                await _store.DeleteAsync(evictedEntry.Id, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Evicted {EvictedCount} oldest embedding memory entries to stay within the {Capacity}-entry FIFO bound.",
                evicted.Count,
                _options.EmbeddingMemoryCapacity);
        }

        return persisted;
    }

    /// <summary>
    /// Finds the top neighbors for a query embedding by cosine similarity, filtered to
    /// <see cref="RoutingOptions.EmbeddingSimilarityThreshold"/> and capped at
    /// <see cref="RoutingOptions.MaxNeighborCount"/>, ordered by descending similarity.
    /// </summary>
    /// <param name="queryEmbedding">The task embedding to find neighbors for.</param>
    public IReadOnlyList<(MemoryEntry Entry, double Similarity)> FindNearest(float[] queryEmbedding)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        List<MemoryEntry> snapshot;
        lock (_syncLock)
        {
            snapshot = [.. _entries];
        }

        return snapshot
            .Select(entry => (Entry: entry, Similarity: CosineSimilarity(queryEmbedding, entry.TaskEmbedding)))
            .Where(candidate => candidate.Similarity >= _options.EmbeddingSimilarityThreshold)
            .OrderByDescending(candidate => candidate.Similarity)
            .Take(_options.MaxNeighborCount)
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
}
