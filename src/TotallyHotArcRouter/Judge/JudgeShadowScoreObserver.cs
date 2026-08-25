using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The shadow judge's <see cref="IQualityScoreObserver"/> (docs/router/geval-shadow-scoring-plan.md §1c).
/// Registered unconditionally as a third element of <see cref="Router.CompositeRouterScoreObserver"/>'s
/// fan-out and gated per call on <see cref="JudgeOptions.Enabled"/> instead, so the System Settings
/// window's judge toggle takes effect immediately rather than at the next restart - the same live-gate
/// posture <c>ProxyMiddleware</c> takes for <c>EnableAdaptiveRouting</c>.
/// <see cref="ObserveAsync"/> does two cheap things and returns immediately - it never calls the judge
/// backbone inline: snapshot the fields needed from <see cref="QualityResult"/> into a
/// <see cref="JudgeShadowScoringJob"/>, then enqueue it onto a bounded channel
/// (<see cref="IJudgeShadowScoreQueue"/>). A full channel sheds the job with a debug log rather than
/// blocking the caller - the routing hot path must never wait on judging.
/// </summary>
public sealed class JudgeShadowScoreObserver : IQualityScoreObserver
{
    private readonly IJudgeShadowScoreQueue _queue;
    private readonly IOptionsMonitor<JudgeOptions> _options;
    private readonly ILogger<JudgeShadowScoreObserver> _logger;

    /// <summary>Initializes a new instance of the <see cref="JudgeShadowScoreObserver"/> class.</summary>
    /// <param name="queue">The bounded queue the drain worker reads from.</param>
    /// <param name="options">Supplies the live <see cref="JudgeOptions.Enabled"/> gate, read per call rather than captured.</param>
    /// <param name="logger">The logger.</param>
    public JudgeShadowScoreObserver(
        IJudgeShadowScoreQueue queue,
        IOptionsMonitor<JudgeOptions> options,
        ILogger<JudgeShadowScoreObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!_options.CurrentValue.Enabled)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(result.RequestCorrelationId))
        {
            _logger.LogDebug("Quality result has no correlation id; skipping shadow-judge observation.");
            return Task.CompletedTask;
        }

        var job = new JudgeShadowScoringJob(
            result.RequestCorrelationId,
            result.Dimension,
            result.Model,
            result.UnifiedScore);

        if (!_queue.TryEnqueue(job))
        {
            _logger.LogDebug(
                "Shadow-judge queue is full; dropped job for correlation {CorrelationId}.",
                result.RequestCorrelationId);
        }

        return Task.CompletedTask;
    }
}
