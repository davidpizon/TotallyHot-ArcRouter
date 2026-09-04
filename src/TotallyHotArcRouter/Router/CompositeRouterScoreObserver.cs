using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Fans a single scored <see cref="QualityResult"/> out to every registered observer -
/// <see cref="RouterMemoryScoreObserver"/> and <see cref="EmbeddingMemoryScoreObserver"/>
/// (docs/router/live-feedback-learning-plan.md Phase 2c) - since <see cref="IQualityScoreObserver"/>
/// otherwise resolves to a single implementation. One observer throwing does not stop the others: each is
/// invoked independently and a failure is logged, matching every other off-path observation in this
/// codebase's "never let a learning-path failure look like a routing failure" convention.
/// </summary>
public sealed class CompositeRouterScoreObserver : IQualityScoreObserver
{
    private readonly ILogger<CompositeRouterScoreObserver> _logger;
    private readonly IReadOnlyList<IQualityScoreObserver> _observers;

    /// <summary>Initializes a new instance of the <see cref="CompositeRouterScoreObserver"/> class.</summary>
    /// <param name="observers">The observers to fan out to, in invocation order.</param>
    /// <param name="logger">The logger.</param>
    public CompositeRouterScoreObserver(IReadOnlyList<IQualityScoreObserver> observers,
        ILogger<CompositeRouterScoreObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(logger);

        _observers = observers;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (var observer in _observers)
            try
            {
                await observer.ObserveAsync(result: result, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception: ex,
                    message:
                    "Router score observer {ObserverType} threw while observing a quality result; continuing with the remaining observers.",
                    observer.GetType().Name);
            }
    }
}