using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The host's <see cref="IPortfolioGraderAvailability"/>: tells the quality aggregator to hold a static
/// verdict open for each of CodeJudge/ICE-Score/RACE only when that grader is switched on <em>and</em> a
/// backbone is actually resolvable - the exact same two-part test <see cref="JudgeAvailability"/> applies
/// for the G-Eval judge, since all four graders share <see cref="JudgeModelSelector"/>.
/// </summary>
public sealed class PortfolioGraderAvailability : IPortfolioGraderAvailability
{
    private readonly JudgeModelSelector _modelSelector;
    private readonly IOptionsMonitor<PortfolioGraderOptions> _options;

    /// <summary>Initializes a new instance of the <see cref="PortfolioGraderAvailability"/> class.</summary>
    /// <param name="options">Supplies the live per-grader enabled gates, read per call rather than captured.</param>
    /// <param name="modelSelector">Resolves the free model that would serve as every portfolio grader's shared backbone.</param>
    public PortfolioGraderAvailability(IOptionsMonitor<PortfolioGraderOptions> options, JudgeModelSelector modelSelector)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(modelSelector);

        _options = options;
        _modelSelector = modelSelector;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> DetermineGraderKeys(QualityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = _options.CurrentValue;

        // The backbone check is resolved at most once, and only if at least one flag is on - an operator
        // running with all three off pays nothing for a resolution whose result would be discarded anyway.
        if (!current.AnyEnabled) return pending;

        var hasBackbone = _modelSelector.Resolve() is not null;
        if (!hasBackbone) return pending;

        if (current.CodeJudgeEnabled) pending.Add(GraderKeys.CodeJudge);
        if (current.IceScoreEnabled) pending.Add(GraderKeys.IceScore);
        if (current.RaceEnabled) pending.Add(GraderKeys.Race);

        return pending;
    }
}
