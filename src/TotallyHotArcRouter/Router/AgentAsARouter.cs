using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Selects the best model for a given routing dimension from <see cref="RouterMemory"/>'s observed scores -
/// selection-only, per PLAN.md Phase I: this class never invokes a model, it only chooses one. The general
/// (non-utility) backing engine <see cref="AgentRouterPolicy"/> delegates to for the <c>agentic-router</c>
/// alias (<c>docs/router/utility-model-routing.md</c> §B3's "General" case).
/// </summary>
public class AgentAsARouter
{
    private readonly ILogger<AgentAsARouter> _logger;
    private readonly RouterMemory _memory;
    private readonly RoutingOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentAsARouter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="options">The routing options.</param>
    /// <param name="memory">The router memory.</param>
    public AgentAsARouter(
        ILogger<AgentAsARouter> logger,
        IOptions<RoutingOptions> options,
        RouterMemory memory)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(memory);

        _logger = logger;
        _options = options.Value;
        _memory = memory;
    }

    /// <summary>
    /// Selects the best available model for a routing dimension without invoking it.
    /// </summary>
    /// <param name="dimension">The routing dimension to select a model for (e.g., 'code_generation', 'translation').</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="RoutingDecision"/> describing the chosen model.</returns>
    public Task<RoutingDecision> SelectModelAsync(string dimension, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation(message: "Selecting model for dimension '{Dimension}'.", dimension);

        // Exploration vs. Exploitation
        var decision = _options.EnableExploration && Random.Shared.NextDouble() < _options.ExplorationRate
            ? SelectExploration(dimension)
            : SelectExploitation(dimension);

        return Task.FromResult(decision);
    }

    /// <summary>
    /// Selects the historically best-performing model for the dimension, falling back to the default model when no model has a
    /// historical score.
    /// </summary>
    private RoutingDecision SelectExploitation(string dimension)
    {
        var models = _memory.GetModelsForDimension(dimension)
            .Select(model => new { Model = model, Score = _memory.GetAverageScore(dimension: dimension, model: model) })
            .Where(m => m.Score.HasValue)
            .OrderByDescending(m => m.Score)
            .Select(m => m.Model)
            .ToList();

        if (models.Count == 0)
        {
            _logger.LogWarning(
                message: "No models with historical scores for dimension '{Dimension}'. Falling back to default model.",
                dimension);
            return RoutingDecision.CreateFallback(_options.DefaultModel);
        }

        var candidateScores = models.ToDictionary(keySelector: m => m,
            elementSelector: m => _memory.GetAverageScore(dimension: dimension, model: m) ?? 0);
        var bestModel = models[0];

        return new RoutingDecision(
            selectedModel: bestModel,
            confidence: candidateScores[bestModel],
            rationale:
            $"Selected best model based on historical performance (avg score: {candidateScores[bestModel]:F2}).",
            timestampUtc: DateTimeOffset.UtcNow,
            candidateScores: candidateScores);
    }

    /// <summary>
    /// Selects a randomly chosen supported model to gather new performance data with neutral confidence.
    /// </summary>
    private RoutingDecision SelectExploration(string dimension)
    {
        var model = RouterConstants.SupportedModels[Random.Shared.Next(RouterConstants.SupportedModels.Count)];
        _logger.LogInformation(message: "Exploring with model '{Model}' for dimension '{Dimension}'.", model,
            dimension);

        return new RoutingDecision(
            selectedModel: model,
            0.5, // Exploration has neutral confidence
            rationale: "Exploration: randomly selected model to gather new data.",
            timestampUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Observes the outcome of a routing decision and updates the memory.
    /// </summary>
    /// <param name="dimension">The dimension of the prompt.</param>
    /// <param name="model">The model that was used.</param>
    /// <param name="score">The quality score of the model's response (0.0 to 1.0).</param>
    public async Task ObserveAsync(string dimension, string model, double score)
    {
        if (score < 0.0 || score > 1.0)
            throw new ArgumentOutOfRangeException(paramName: nameof(score),
                message: "Score must be between 0.0 and 1.0.");

        _logger.LogInformation(message: "Observing score {Score} for model '{Model}' in dimension '{Dimension}'.",
            score, model, dimension);
        await _memory.AddScoreAsync(dimension: dimension, model: model, score: score);
    }
}