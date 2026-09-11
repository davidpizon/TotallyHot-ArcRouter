using System.ComponentModel.DataAnnotations;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Thresholds for Phase G2's score-collapse verdict
/// (docs/router/geval-shadow-scoring-plan.md Phase G2). Bound from the <c>JudgeCalibration</c> section of
/// <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both defaults are documented guesses, not measured values</b> - the same honesty
/// <c>RoutingOptions.JudgeScoredRowWeight</c>'s 0.5 shipped with. G-Eval
/// (docs/research/2303.16634v3.md) documents score collapse as a real failure mode of exactly this
/// technique but publishes no threshold for it, so these numbers exist to make the check <i>runnable</i>,
/// and re-tuning them is a configuration edit rather than a code change.
/// </para>
/// <para>
/// They are also not the only thing making the check meaningful. Because
/// <see cref="JudgeCalibrationAnalyzer"/> evaluates every cohort separately, a report ordinarily contains
/// both probability-weighted and single-sample cohorts, and comparing those two directly answers "is the
/// fallback flatter?" without reference to any threshold at all. The thresholds cover the case where that
/// comparison is unavailable - every cohort is a fallback cohort, so there is nothing healthy to compare
/// against.
/// </para>
/// </remarks>
public sealed class JudgeCalibrationOptions
{
    /// <summary>The configuration section this type binds from.</summary>
    public const string SectionName = "JudgeCalibration";

    /// <summary>
    /// Gets the smallest population standard deviation a cohort's judge scores may have before the
    /// score-collapse verdict fails. Defaults to <c>0.05</c> on the <c>[0,1]</c> normalized scale - a judge
    /// whose entire output fits inside a five-hundredths-wide band is answering the same thing regardless
    /// of input, whatever its mean looks like.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinimumStandardDeviation { get; init; } = 0.05;

    /// <summary>
    /// Gets the largest share of a cohort its single most common score may take before the score-collapse
    /// verdict fails. Defaults to <c>0.80</c>. Catches the case a standard deviation alone misses - a
    /// distribution that is 80% one digit plus a few outliers wide enough to keep the spread respectable,
    /// which is precisely the shape G-Eval's ties problem produces on an integer-emitting backbone.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MaximumModalShare { get; init; } = 0.80;
}
