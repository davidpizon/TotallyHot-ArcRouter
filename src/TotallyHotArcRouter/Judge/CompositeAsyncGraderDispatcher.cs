using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Fans <see cref="DispatchAsync"/> out to every registered <see cref="IAsyncGraderDispatcher"/> - today
/// <see cref="JudgeShadowScoreDispatcher"/> (the G-Eval judge) and <see cref="PortfolioGraderDispatcher"/>
/// (Phase Q3's CodeJudge/ICE-Score/RACE) - and unions their accepted grader-key sets, since
/// <see cref="Quality.Grading.QualityScoreAggregator"/> takes exactly one
/// <see cref="IAsyncGraderDispatcher"/>. One dispatcher's failure is logged and treated as "accepted
/// nothing" rather than failing the whole call, so a broken portfolio-grader dispatch can never also take
/// down the judge's.
/// </summary>
public sealed class CompositeAsyncGraderDispatcher : IAsyncGraderDispatcher
{
    private readonly IReadOnlyList<IAsyncGraderDispatcher> _dispatchers;
    private readonly ILogger<CompositeAsyncGraderDispatcher> _logger;

    /// <summary>Initializes a new instance of the <see cref="CompositeAsyncGraderDispatcher"/> class.</summary>
    /// <param name="dispatchers">Every dispatcher to fan out to.</param>
    /// <param name="logger">The logger.</param>
    public CompositeAsyncGraderDispatcher(
        IReadOnlyList<IAsyncGraderDispatcher> dispatchers,
        ILogger<CompositeAsyncGraderDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatchers);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatchers = dispatchers;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> DispatchAsync(
        QualityResult result,
        IReadOnlySet<string> pendingGraderKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(pendingGraderKeys);

        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dispatcher in _dispatchers)
        {
            IReadOnlySet<string> dispatcherAccepted;
            try
            {
                dispatcherAccepted = await dispatcher
                    .DispatchAsync(result: result, pendingGraderKeys: pendingGraderKeys,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception: ex,
                    message:
                    "A grader dispatcher failed for correlation {CorrelationId}; treating it as accepting nothing.",
                    result.RequestCorrelationId);
                continue;
            }

            accepted.UnionWith(dispatcherAccepted);
        }

        return accepted;
    }
}
