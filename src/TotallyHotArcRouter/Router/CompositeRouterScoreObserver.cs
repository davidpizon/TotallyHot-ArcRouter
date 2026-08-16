using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Fans a single scored <see cref="SandboxResult"/> out to every registered observer -
/// <see cref="RouterMemoryScoreObserver"/> and <see cref="EmbeddingMemoryScoreObserver"/>
/// (docs/router/live-feedback-learning-plan.md Phase 2c) - since <see cref="IRouterScoreObserver"/>
/// otherwise resolves to a single implementation. One observer throwing does not stop the others: each is
/// invoked independently and a failure is logged, matching every other off-path observation in this
/// codebase's "never let a learning-path failure look like a routing failure" convention.
/// </summary>
public sealed class CompositeRouterScoreObserver : IRouterScoreObserver
{
    private readonly IReadOnlyList<IRouterScoreObserver> _observers;
    private readonly ILogger<CompositeRouterScoreObserver> _logger;

    /// <summary>Initializes a new instance of the <see cref="CompositeRouterScoreObserver"/> class.</summary>
    /// <param name="observers">The observers to fan out to, in invocation order.</param>
    /// <param name="logger">The logger.</param>
    public CompositeRouterScoreObserver(IReadOnlyList<IRouterScoreObserver> observers, ILogger<CompositeRouterScoreObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(logger);

        _observers = observers;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ObserveAsync(SandboxResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (var observer in _observers)
        {
            try
            {
                await observer.ObserveAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Router score observer {ObserverType} threw while observing a sandbox result; continuing with the remaining observers.",
                    observer.GetType().Name);
            }
        }
    }
}
