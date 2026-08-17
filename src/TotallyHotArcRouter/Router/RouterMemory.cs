using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Represents the memory of the router, storing scores for different models.
/// </summary>
public class RouterMemory
{
    /// <summary>The in-memory scores, keyed by dimension then model. Reassigned wholesale by <see cref="InitializeAsync"/>, so callers must not cache a reference across that call.</summary>
    private ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>> _scores;

    /// <summary>The optional persistence layer; <see langword="null"/> when this memory is in-memory-only (e.g. in tests).</summary>
    private readonly IRouterMemoryStore? _memoryStore;

    /// <summary>The optional logger; <see langword="null"/> when logging is not configured for this instance.</summary>
    private readonly ILogger<RouterMemory>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterMemory"/> class.
    /// </summary>
    /// <param name="memoryStore">The memory store to use for persistence.</param>
    /// <param name="logger">The logger.</param>
    public RouterMemory(IRouterMemoryStore? memoryStore = null, ILogger<RouterMemory>? logger = null)
    {
        _scores = new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>();
        _memoryStore = memoryStore;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the memory by loading it from the store.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_memoryStore != null)
        {
            _logger?.LogInformation("Initializing router memory from store.");
            _scores = await _memoryStore.LoadAsync();
        }
    }

    /// <summary>
    /// Adds a score for a given model and dimension.
    /// </summary>
    /// <param name="dimension">The dimension to which the score belongs.</param>
    /// <param name="model">The model for which the score is recorded.</param>
    /// <param name="score">The score to add.</param>
    public async Task AddScoreAsync(string dimension, string model, double score)
    {
        var dimensionScores = _scores.GetOrAdd(dimension, new ConcurrentDictionary<string, List<double>>());
        var modelScores = dimensionScores.GetOrAdd(model, new List<double>());

        lock (modelScores)
        {
            modelScores.Add(score);
        }

        if (_memoryStore != null)
        {
            await _memoryStore.SaveAsync(_scores);
        }
    }

    /// <summary>
    /// Gets the average score for a given model and dimension.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="model">The model.</param>
    /// <returns>The average score, or null if no scores are available.</returns>
    public double? GetAverageScore(string dimension, string model)
    {
        if (_scores.TryGetValue(dimension, out var dimensionScores) &&
            dimensionScores.TryGetValue(model, out var modelScores))
        {
            lock (modelScores)
            {
                return modelScores.Count > 0 ? modelScores.Average() : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all models for a given dimension.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <returns>A collection of model names.</returns>
    public IEnumerable<string> GetModelsForDimension(string dimension)
    {
        if (_scores.TryGetValue(dimension, out var dimensionScores))
        {
            return dimensionScores.Keys;
        }

        return Enumerable.Empty<string>();
    }
}

