using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Provides an in-memory vector-store-like implementation of <see cref="IRouterMemoryStore"/>.
/// </summary>
public sealed class VectorStoreRouterMemoryStore : IRouterMemoryStore
{
    private readonly object _syncLock = new();
    private ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>> _memory = new();

    /// <summary>
    /// Loads the currently stored memory snapshot.
    /// </summary>
    /// <returns>A deep-copied memory snapshot.</returns>
    public Task<ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>> LoadAsync()
    {
        lock (_syncLock)
        {
            return Task.FromResult(CloneMemory(_memory));
        }
    }

    /// <summary>
    /// Saves a memory snapshot to the in-memory store.
    /// </summary>
    /// <param name="memory">Memory data to persist.</param>
    public Task SaveAsync(ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>> memory)
    {
        lock (_syncLock)
        {
            _memory = CloneMemory(memory);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Finds model candidates for tasks similar to the provided task description.
    /// Similarity is approximated by token overlap with known dimensions.
    /// </summary>
    /// <param name="taskDescription">Task text used for similarity matching.</param>
    /// <param name="topK">Maximum number of models to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Model-score pairs ordered by descending score.</returns>
    public Task<IReadOnlyList<(string Model, double Score)>> FindSimilarAsync(
        string taskDescription,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        if (topK <= 0)
        {
            return Task.FromResult<IReadOnlyList<(string Model, double Score)>>(Array.Empty<(string Model, double Score)>());
        }

        cancellationToken.ThrowIfCancellationRequested();

        var taskTokens = Tokenize(taskDescription);
        var aggregate = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        lock (_syncLock)
        {
            foreach (var dimension in _memory)
            {
                var dimensionTokens = Tokenize(dimension.Key);
                var similarityWeight = CalculateJaccardSimilarity(taskTokens, dimensionTokens);
                if (similarityWeight <= 0)
                {
                    continue;
                }

                foreach (var modelEntry in dimension.Value)
                {
                    if (modelEntry.Value.Count == 0)
                    {
                        continue;
                    }

                    var averageScore = modelEntry.Value.Average();
                    var weightedScore = averageScore * similarityWeight;

                    if (aggregate.TryGetValue(modelEntry.Key, out var current))
                    {
                        aggregate[modelEntry.Key] = Math.Max(current, weightedScore);
                    }
                    else
                    {
                        aggregate[modelEntry.Key] = weightedScore;
                    }
                }
            }
        }

        var result = aggregate
            .OrderByDescending(kvp => kvp.Value)
            .Take(topK)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToArray();

        return Task.FromResult<IReadOnlyList<(string Model, double Score)>>(result);
    }

    /// <summary>
    /// Creates a deep copy of the nested dimension-to-model score dictionary so callers can mutate the clone without affecting the source.
    /// </summary>
    private static ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>> CloneMemory(
        ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>> source)
    {
        var clone = new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>(StringComparer.Ordinal);

        foreach (var dimensionEntry in source)
        {
            var models = new ConcurrentDictionary<string, List<double>>(StringComparer.Ordinal);
            foreach (var modelEntry in dimensionEntry.Value)
            {
                models[modelEntry.Key] = [.. modelEntry.Value];
            }

            clone[dimensionEntry.Key] = models;
        }

        return clone;
    }

    /// <summary>
    /// Splits the input on common delimiters and lower-cases the result into a set of unique tokens for similarity comparison.
    /// </summary>
    private static HashSet<string> Tokenize(string input)
    {
        return input
            .Split([' ', '_', '-', '.', '/', '\\', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Computes the Jaccard similarity (intersection size over union size) between two token sets, returning 0 when either set is empty or there is no overlap.
    /// </summary>
    private static double CalculateJaccardSimilarity(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersectionCount = left.Intersect(right).Count();
        if (intersectionCount == 0)
        {
            return 0;
        }

        var unionCount = left.Union(right).Count();
        return unionCount == 0 ? 0 : (double)intersectionCount / unionCount;
    }
}

