using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The single <see cref="IAsyncGraderDispatcher"/> for every LLM grader (G-Eval judge and the
/// CodeJudge/ICE-Score/RACE portfolio). Registered unconditionally and gated per key on the live
/// <see cref="JudgeOptions.Enabled"/> / <see cref="PortfolioGraderOptions"/> flags, so System Settings
/// toggles take effect immediately. <see cref="DispatchAsync"/> enqueues cheap snapshots onto
/// <see cref="IGraderQueue"/> and returns; it never calls a backbone inline.
/// </summary>
public sealed class GraderDispatcher : IAsyncGraderDispatcher
{
    /// <summary>
    /// Every grader key this dispatcher can enqueue. <see cref="GraderDrainService"/> runs one lane
    /// consumer per key from this same list, so a dispatchable key can never lack a reader.
    /// </summary>
    internal static readonly IReadOnlyList<string> DispatchableKeys =
    [
        GraderKeys.Judge, GraderKeys.CodeJudge, GraderKeys.IceScore, GraderKeys.Race
    ];

    private readonly IOptionsMonitor<JudgeOptions> _judgeOptions;
    private readonly ILogger<GraderDispatcher> _logger;
    private readonly IOptionsMonitor<PortfolioGraderOptions> _portfolioOptions;
    private readonly IGraderQueue _queue;

    /// <summary>Initializes a new instance of the <see cref="GraderDispatcher"/> class.</summary>
    /// <param name="queue">The bounded queue the drain worker reads from.</param>
    /// <param name="judgeOptions">Supplies the live <see cref="JudgeOptions.Enabled"/> gate, read per call.</param>
    /// <param name="portfolioOptions">Supplies the live per-portfolio-grader enabled gates, read per call.</param>
    /// <param name="logger">The logger.</param>
    public GraderDispatcher(
        IGraderQueue queue,
        IOptionsMonitor<JudgeOptions> judgeOptions,
        IOptionsMonitor<PortfolioGraderOptions> portfolioOptions,
        ILogger<GraderDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(judgeOptions);
        ArgumentNullException.ThrowIfNull(portfolioOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _judgeOptions = judgeOptions;
        _portfolioOptions = portfolioOptions;
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
            _logger.LogDebug("Quality result has no correlation id; skipping grader dispatch.");
            return Task.FromResult((IReadOnlySet<string>)accepted);
        }

        var judge = _judgeOptions.CurrentValue;
        var portfolio = _portfolioOptions.CurrentValue;
        foreach (var graderKey in DispatchableKeys)
        {
            if (!pendingGraderKeys.Contains(graderKey) || !IsEnabled(graderKey, judge, portfolio)) continue;

            var job = new GraderScoringJob(
                CorrelationId: result.RequestCorrelationId,
                GraderKey: graderKey,
                Dimension: result.Dimension,
                Model: result.Model,
                StaticScore: result.UnifiedScore,
                SyntaxAuthoritative: result.SyntaxAuthoritative);

            if (_queue.TryEnqueue(job))
            {
                accepted.Add(graderKey);
            }
            else
            {
                _logger.LogDebug(
                    message: "Grader queue is full; dropped {GraderKey} job for correlation {CorrelationId}.",
                    graderKey,
                    result.RequestCorrelationId);
            }
        }

        return Task.FromResult((IReadOnlySet<string>)accepted);
    }

    /// <summary>Returns whether <paramref name="graderKey"/> is currently enabled, re-read live rather than captured.</summary>
    internal static bool IsEnabled(string graderKey, JudgeOptions judge, PortfolioGraderOptions portfolio)
    {
        if (string.Equals(graderKey, GraderKeys.Judge, StringComparison.OrdinalIgnoreCase)) return judge.Enabled;
        if (string.Equals(graderKey, GraderKeys.CodeJudge, StringComparison.OrdinalIgnoreCase)) return portfolio.CodeJudgeEnabled;
        if (string.Equals(graderKey, GraderKeys.IceScore, StringComparison.OrdinalIgnoreCase)) return portfolio.IceScoreEnabled;
        if (string.Equals(graderKey, GraderKeys.Race, StringComparison.OrdinalIgnoreCase)) return portfolio.RaceEnabled;
        return false;
    }
}
