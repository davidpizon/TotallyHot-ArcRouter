using TotallyHot.ArcRouter.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// An intelligent router that selects the best model for a given prompt.
/// </summary>
public class AgentAsARouter
{
    private readonly ILogger<AgentAsARouter> _logger;
    private readonly RoutingOptions _options;
    private readonly IRouterModelClient _modelClient;
    private readonly RouterMemory _memory;
    private readonly Random _random = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentAsARouter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="options">The routing options.</param>
    /// <param name="modelClient">The model client.</param>
    /// <param name="memory">The router memory.</param>
    public AgentAsARouter(
        ILogger<AgentAsARouter> logger,
        IOptions<RoutingOptions> options,
        IRouterModelClient modelClient,
        RouterMemory memory)
    {
        _logger = logger;
        _options = options.Value;
        _modelClient = modelClient;
        _memory = memory;
    }

    /// <summary>
    /// Routes a prompt to the best available model.
    /// </summary>
    /// <param name="prompt">The prompt to route.</param>
    /// <param name="dimension">The dimension of the prompt (e.g., 'code_generation', 'translation').</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="RoutingResult"/>.</returns>
    public async Task<RoutingResult> RouteAsync(string prompt, string dimension, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Routing prompt for dimension '{Dimension}'.", dimension);

        // Exploration vs. Exploitation
        if (_options.EnableExploration && _random.NextDouble() < _options.ExplorationRate)
        {
            return await ExploreAsync(prompt, dimension, cancellationToken);
        }

        return await ExploitAsync(prompt, dimension, cancellationToken);
    }

    /// <summary>
    /// Routes the prompt to the historically best-performing model for the dimension, falling back to the default model when no model has a historical score.
    /// </summary>
    private async Task<RoutingResult> ExploitAsync(string prompt, string dimension, CancellationToken cancellationToken)
    {
        var models = _memory.GetModelsForDimension(dimension)
            .Select(model => new { Model = model, Score = _memory.GetAverageScore(dimension, model) })
            .Where(m => m.Score.HasValue)
            .OrderByDescending(m => m.Score)
            .Select(m => m.Model)
            .ToList();

        var candidateScores = models.ToDictionary(m => m, m => _memory.GetAverageScore(dimension, m) ?? 0);

        if (models.Count == 0)
        {
            _logger.LogWarning("No models with historical scores for dimension '{Dimension}'. Falling back to default model.", dimension);
            var response = await _modelClient.GetResponseAsync(_options.DefaultModel, prompt, cancellationToken);
            return new RoutingResult
            {
                Decision = RoutingDecision.CreateFallback(_options.DefaultModel),
                Response = response
            };
        }

        foreach (var model in models)
        {
            try
            {
                var response = await _modelClient.GetResponseAsync(model, prompt, cancellationToken);
                var decision = new RoutingDecision(
                    model,
                    _memory.GetAverageScore(dimension, model) ?? 0,
                    $"Selected best model based on historical performance (avg score: {_memory.GetAverageScore(dimension, model):F2}).",
                    DateTimeOffset.UtcNow,
                    candidateScores);

                return new RoutingResult
                {
                    Decision = decision,
                    Response = response
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Model '{Model}' failed to respond. Trying next best model.", model);
            }
        }

        _logger.LogError("All models failed to respond for dimension '{Dimension}'. Falling back to default model.", dimension);
        var fallbackResponse = await _modelClient.GetResponseAsync(_options.DefaultModel, prompt, cancellationToken);
        return new RoutingResult
        {
            Decision = RoutingDecision.CreateFallback(_options.DefaultModel),
            Response = fallbackResponse
        };
    }

    /// <summary>
    /// Routes the prompt to a randomly selected supported model to gather new performance data with neutral confidence.
    /// </summary>
    private async Task<RoutingResult> ExploreAsync(string prompt, string dimension, CancellationToken cancellationToken)
    {
        var model = RouterConstants.SupportedModels[_random.Next(RouterConstants.SupportedModels.Count)];
        _logger.LogInformation("Exploring with model '{Model}' for dimension '{Dimension}'.", model, dimension);

        var response = await _modelClient.GetResponseAsync(model, prompt, cancellationToken);
        var decision = new RoutingDecision(
            model,
            0.5, // Exploration has neutral confidence
            "Exploration: randomly selected model to gather new data.",
            DateTimeOffset.UtcNow);

        return new RoutingResult
        {
            Decision = decision,
            Response = response
        };
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
        {
            throw new ArgumentOutOfRangeException(nameof(score), "Score must be between 0.0 and 1.0.");
        }

        _logger.LogInformation("Observing score {Score} for model '{Model}' in dimension '{Dimension}'.", score, model, dimension);
        await _memory.AddScoreAsync(dimension, model, score);
    }
}

