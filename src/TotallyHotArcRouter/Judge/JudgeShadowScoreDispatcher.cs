using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The shadow judge's <see cref="IAsyncGraderDispatcher"/> (docs/router/geval-shadow-scoring-plan.md §1c;
/// docs/router/judge-join-deadlock-fix-plan.md). Registered unconditionally with the host's DI container and
/// gated per call on <see cref="JudgeOptions.Enabled"/> instead, so the System Settings window's judge
/// toggle takes effect immediately rather than at the next restart - the same live-gate posture
/// <c>ProxyMiddleware</c> takes for <c>EnableAdaptiveRouting</c>.
/// <see cref="DispatchAsync"/> does two cheap things and returns immediately - it never calls the judge
/// backbone inline: snapshot the fields needed from <see cref="QualityResult"/> into a
/// <see cref="JudgeShadowScoringJob"/>, then enqueue it onto a bounded channel
/// (<see cref="IJudgeShadowScoreQueue"/>). A full channel sheds the job with a debug log rather than
/// blocking the caller - the routing hot path must never wait on judging.
/// </summary>
/// <remarks>
/// This type used to be <c>JudgeShadowScoreObserver</c>, an <see cref="Quality.Grading.IQualityScoreObserver"/>
/// invoked when <see cref="Quality.Grading.QualityScoreAggregator"/> wrote a result. That trigger point
/// deadlocked under the hold-based aggregator: a result needing judgment is never written until the judge
/// resolves it, so an observer that only fires at the write can never start the judge that write is waiting
/// on. Every judged request paid the full join timeout for a grade nobody had actually requested yet
/// (docs/router/judge-join-deadlock-fix-plan.md). This type now implements
/// <see cref="IAsyncGraderDispatcher"/> instead, started by <see cref="Quality.Grading.QualityScoreAggregator.SubmitAsync"/>
/// at the moment the hold opens, and is deliberately absent from
/// <see cref="Router.CompositeRouterScoreObserver"/>'s fan-out - it no longer implements
/// <see cref="Quality.Grading.IQualityScoreObserver"/> at all.
/// </remarks>
public sealed class JudgeShadowScoreDispatcher : IAsyncGraderDispatcher
{
    private readonly ILogger<JudgeShadowScoreDispatcher> _logger;
    private readonly IOptionsMonitor<JudgeOptions> _options;
    private readonly IJudgeShadowScoreQueue _queue;

    /// <summary>Initializes a new instance of the <see cref="JudgeShadowScoreDispatcher"/> class.</summary>
    /// <param name="queue">The bounded queue the drain worker reads from.</param>
    /// <param name="options">Supplies the live <see cref="JudgeOptions.Enabled"/> gate, read per call rather than captured.</param>
    /// <param name="logger">The logger.</param>
    public JudgeShadowScoreDispatcher(
        IJudgeShadowScoreQueue queue,
        IOptionsMonitor<JudgeOptions> options,
        ILogger<JudgeShadowScoreDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<IReadOnlySet<string>> DispatchAsync(
        QualityResult result,
        IReadOnlySet<string> pendingGraderKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(pendingGraderKeys);

        var none = (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!pendingGraderKeys.Contains(GraderKeys.Judge)) return Task.FromResult(none);
        if (!_options.CurrentValue.Enabled) return Task.FromResult(none);

        if (string.IsNullOrEmpty(result.RequestCorrelationId))
        {
            _logger.LogDebug("Quality result has no correlation id; skipping shadow-judge dispatch.");
            return Task.FromResult(none);
        }

        var job = new JudgeShadowScoringJob(
            CorrelationId: result.RequestCorrelationId,
            Dimension: result.Dimension,
            Model: result.Model,
            StaticScore: result.UnifiedScore);

        if (!_queue.TryEnqueue(job))
        {
            _logger.LogDebug(
                message: "Shadow-judge queue is full; dropped job for correlation {CorrelationId}.",
                result.RequestCorrelationId);
            return Task.FromResult(none);
        }

        return Task.FromResult((IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { GraderKeys.Judge });
    }
}
