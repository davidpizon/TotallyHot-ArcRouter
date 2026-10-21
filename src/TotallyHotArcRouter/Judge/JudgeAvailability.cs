using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The host's <see cref="IJudgeAvailability"/>: tells the quality aggregator to hold a static verdict open
/// for a judge grade only when the result carries a correlation id, the judge is switched on, <em>and</em>
/// a backbone is actually resolvable.
/// </summary>
/// <remarks>
/// A missing correlation id answers <see langword="false"/> before any backbone work: a grade cannot be
/// joined onto that result. <see cref="QualityScoreAggregator"/> also rejects those before it calls here,
/// so this method is not what skips the resolution on the aggregator's path; it gives a direct caller the
/// same answer. <see cref="JudgeOptions.Enabled"/> is read live through
/// <see cref="IOptionsMonitor{TOptions}"/> rather than captured, so toggling the judge in the System
/// Settings window takes effect on the next request instead of the next restart - the same posture
/// <see cref="GraderDispatcher"/> takes. The backbone check then guards against the case that flag alone
/// cannot see: the judge is enabled, but every free model has been switched off, stopped, or has
/// disappeared upstream. Answering <see langword="true"/> there would make every score wait out the full
/// join timeout before being written unjudged, so the honest answer is that no judge is coming.
/// </remarks>
public sealed class JudgeAvailability : IJudgeAvailability
{
    private readonly JudgeModelSelector _modelSelector;
    private readonly IOptionsMonitor<JudgeOptions> _options;

    /// <summary>Initializes a new instance of the <see cref="JudgeAvailability"/> class.</summary>
    /// <param name="options">Supplies the live <see cref="JudgeOptions.Enabled"/> gate, read per call rather than captured.</param>
    /// <param name="modelSelector">Resolves the free model that would serve as the judge backbone.</param>
    public JudgeAvailability(IOptionsMonitor<JudgeOptions> options, JudgeModelSelector modelSelector)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(modelSelector);

        _options = options;
        _modelSelector = modelSelector;
    }

    /// <inheritdoc/>
    public bool WillJudge(QualityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Read the result, rather than only rejecting null: a grade can only be joined onto a correlation id.
        if (string.IsNullOrEmpty(result.RequestCorrelationId)) return false;

        if (!_options.CurrentValue.Enabled) return false;

        return _modelSelector.Resolve() is not null;
    }
}