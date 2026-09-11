namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Computes a <see cref="JudgeCalibrationReport"/> from accumulated <c>judge_shadow_scores</c> rows
/// (docs/router/geval-shadow-scoring-plan.md Phase G2). Pure and read-only, mirroring
/// <see cref="IGraderReliabilityAnalyzer"/>'s posture exactly: no live API call, no mutation, safe to
/// re-run at will, and it never touches the judge's blend weight or a voter's.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="IGraderReliabilityAnalyzer"/> rather than folded into it. That
/// analyzer reads <c>grader_scores</c> and answers "do the four graders agree with each other?"; this one
/// reads <c>judge_shadow_scores</c> and answers "does the judge agree with the static verifier, and is it
/// still saying something?" - a different table, a different pairing, and a different consumer. Merging
/// them would give one report two unrelated halves whose sample sizes never match.
/// </remarks>
public interface IJudgeCalibrationAnalyzer
{
    /// <summary>
    /// The minimum number of rows a cohort needs before its rank correlation and score-collapse verdict
    /// are computed at all. Below this, the correlation is suppressed (<see langword="null"/>) and the
    /// verdict reports <see cref="JudgeCalibrationVerdictKind.Insufficient"/> rather than a number from too
    /// few points. Shares <see cref="IGraderReliabilityAnalyzer.MinimumSampleSize"/>'s value and its
    /// caveat: a starting guess (30), not a measured threshold.
    /// </summary>
    int MinimumSampleSize { get; }

    /// <summary>Computes the full report over every row currently in <c>judge_shadow_scores</c>.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The report, with empty cohort and self-preference lists when the table holds no rows.</returns>
    Task<JudgeCalibrationReport> AnalyzeAsync(CancellationToken cancellationToken = default);
}
