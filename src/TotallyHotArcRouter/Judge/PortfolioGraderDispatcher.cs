using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Phase Q3's <see cref="IAsyncGraderDispatcher"/> for the CodeJudge/ICE-Score/RACE portfolio. Registered
/// unconditionally and gated per call on each flag in <see cref="PortfolioGraderOptions"/> instead, exactly
/// the live-toggle posture <see cref="JudgeShadowScoreDispatcher"/> takes for the G-Eval judge.
/// <see cref="DispatchAsync"/> enqueues up to three jobs - one per requested, enabled grader key - onto the
/// shared bounded queue and returns immediately; it never calls a grader backbone inline.
/// </summary>
/// <remarks>
/// A single dispatcher covering all three graders, rather than one dispatcher per grader
/// (<see cref="JudgeShadowScoreDispatcher"/>'s one-dispatcher-per-grader shape), because
/// <see cref="IAsyncGraderDispatcher"/> is registered as a single DI service: only the last one registered
/// would ever be resolved if each grader tried to register its own.
/// </remarks>
public sealed class PortfolioGraderDispatcher : IAsyncGraderDispatcher
{
    private readonly ILogger<PortfolioGraderDispatcher> _logger;
    private readonly IOptionsMonitor<PortfolioGraderOptions> _options;
    private readonly IPortfolioGraderQueue _queue;

    /// <summary>Initializes a new instance of the <see cref="PortfolioGraderDispatcher"/> class.</summary>
    /// <param name="queue">The bounded queue the drain worker reads from.</param>
    /// <param name="options">Supplies the live per-grader enabled gates, read per call rather than captured.</param>
    /// <param name="logger">The logger.</param>
    public PortfolioGraderDispatcher(
        IPortfolioGraderQueue queue,
        IOptionsMonitor<PortfolioGraderOptions> options,
        ILogger<PortfolioGraderDispatcher> logger)
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

        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(result.RequestCorrelationId))
        {
            _logger.LogDebug("Quality result has no correlation id; skipping portfolio-grader dispatch.");
            return Task.FromResult((IReadOnlySet<string>)accepted);
        }

        var current = _options.CurrentValue;
        TryDispatchOne(GraderKeys.CodeJudge, current.CodeJudgeEnabled, result, pendingGraderKeys, accepted);
        TryDispatchOne(GraderKeys.IceScore, current.IceScoreEnabled, result, pendingGraderKeys, accepted);
        TryDispatchOne(GraderKeys.Race, current.RaceEnabled, result, pendingGraderKeys, accepted);

        return Task.FromResult((IReadOnlySet<string>)accepted);
    }

    /// <summary>Enqueues one grader's job when it was requested and is enabled, recording acceptance in <paramref name="accepted"/>.</summary>
    private void TryDispatchOne(
        string graderKey,
        bool enabled,
        QualityResult result,
        IReadOnlySet<string> pendingGraderKeys,
        HashSet<string> accepted)
    {
        if (!pendingGraderKeys.Contains(graderKey) || !enabled) return;

        var job = new PortfolioGraderJob(
            CorrelationId: result.RequestCorrelationId,
            GraderKey: graderKey,
            Dimension: result.Dimension);

        if (_queue.TryEnqueue(job))
        {
            accepted.Add(graderKey);
        }
        else
        {
            _logger.LogDebug(
                message: "Portfolio-grader queue is full; dropped {GraderKey} job for correlation {CorrelationId}.",
                graderKey,
                result.RequestCorrelationId);
        }
    }
}
