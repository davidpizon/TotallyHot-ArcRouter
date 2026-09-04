using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Represents the memory of the router, storing scores for different models.
/// </summary>
/// <remarks>
/// Scores are held as running <see cref="ScoreAggregate"/> values rather than the raw observation lists
/// this type previously kept, making <see cref="GetAverageScore"/> an O(1) hot-path read and keeping a
/// persisted snapshot bounded by (dimensions x models) rather than by total observations. Growth is
/// deliberately unbounded in the *number of observations* folded into each aggregate - unlike
/// <see cref="EmbeddingMemory"/>'s FIFO window, this memory is meant to accumulate indefinitely, and with
/// the aggregate shape it can do so at fixed cost. See <see cref="ScoreAggregate"/>.
/// </remarks>
public class RouterMemory
{
    /// <summary>The optional logger; <see langword="null"/> when logging is not configured for this instance.</summary>
    private readonly ILogger<RouterMemory>? _logger;

    /// <summary>The optional persistence layer; <see langword="null"/> when this memory is in-memory-only (e.g. in tests).</summary>
    private readonly IRouterMemoryStore? _memoryStore;

    /// <summary>
    /// The in-memory score aggregates, keyed by dimension then model. Reassigned wholesale by
    /// <see cref="InitializeAsync"/>, so callers must not cache a reference across that call.
    /// </summary>
    private ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>> _scores;

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterMemory"/> class.
    /// </summary>
    /// <param name="memoryStore">The memory store to use for persistence.</param>
    /// <param name="logger">The logger.</param>
    public RouterMemory(IRouterMemoryStore? memoryStore = null, ILogger<RouterMemory>? logger = null)
    {
        _scores = new ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>();
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
            _scores = await _memoryStore.LoadAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds a score for a given model and dimension.
    /// </summary>
    /// <param name="dimension">The dimension to which the score belongs.</param>
    /// <param name="model">The model for which the score is recorded.</param>
    /// <param name="score">The score to add.</param>
    /// <remarks>
    /// The in-memory aggregate is replaced atomically rather than mutated in place, and the store is handed
    /// the single new observation rather than a whole-memory snapshot. Both sides therefore accumulate the
    /// same increments independently and stay in agreement without a shared lock, and a concurrent reader
    /// never observes a partially updated value - see <see cref="ScoreAggregate"/>'s remarks on why
    /// immutability is required here rather than merely preferred.
    /// </remarks>
    public async Task AddScoreAsync(string dimension, string model, double score)
    {
        var dimensionScores = _scores.GetOrAdd(
            key: dimension,
            valueFactory: static _ => new ConcurrentDictionary<string, ScoreAggregate>());

        dimensionScores.AddOrUpdate(
            key: model,
            addValueFactory: static (_, newScore) => new ScoreAggregate(Sum: newScore, 1),
            updateValueFactory: static (_, existing, newScore) => existing.Add(newScore),
            factoryArgument: score);

        if (_memoryStore != null)
            await _memoryStore.RecordScoreAsync(dimension: dimension, model: model, score: score).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the average score for a given model and dimension.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="model">The model.</param>
    /// <returns>The average score, or null if no scores are available.</returns>
    public double? GetAverageScore(string dimension, string model)
    {
        if (_scores.TryGetValue(key: dimension, value: out var dimensionScores) &&
            dimensionScores.TryGetValue(key: model, value: out var aggregate))
            return aggregate.Average;

        return null;
    }

    /// <summary>
    /// Gets how many scores have been observed for a given model and dimension, or <c>0</c> when the pair
    /// is unknown.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="model">The model.</param>
    /// <returns>The observation count backing <see cref="GetAverageScore"/>'s mean.</returns>
    /// <remarks>
    /// Exposed because the <see cref="ScoreAggregate"/> shape now tracks it for free, and because a caller
    /// weighing a live average against an offline prior needs to know whether that average rests on one
    /// observation or a thousand. <see cref="Orchestrator.DimBestVoter"/> records the absence of this
    /// accessor as its reason for preferring live scores outright rather than shrinking toward the prior;
    /// adopting a sample-size-aware blend there remains a separate behavioral decision, not something this
    /// accessor's existence settles.
    /// </remarks>
    public int GetObservationCount(string dimension, string model)
    {
        if (_scores.TryGetValue(key: dimension, value: out var dimensionScores) &&
            dimensionScores.TryGetValue(key: model, value: out var aggregate))
            return aggregate.Count;

        return 0;
    }

    /// <summary>
    /// Gets all models for a given dimension.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <returns>A collection of model names.</returns>
    public IEnumerable<string> GetModelsForDimension(string dimension)
    {
        if (_scores.TryGetValue(key: dimension, value: out var dimensionScores)) return dimensionScores.Keys;

        return [];
    }
}