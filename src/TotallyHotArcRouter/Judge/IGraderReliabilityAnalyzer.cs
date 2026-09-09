namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Computes a <see cref="GraderReliabilityReport"/> from accumulated <c>grader_scores</c> rows
/// (docs/router/grader-reliability-plan.md, Phase Q4). Pure and read-only, mirroring
/// <c>IRegretHarnessRunner</c>'s posture: no live API call, no mutation, safe to re-run at will and never
/// touches a live voter's weight.
/// </summary>
public interface IGraderReliabilityAnalyzer
{
    /// <summary>
    /// The minimum number of paired observations a correlation or self-preference comparison needs before
    /// it is reported at all. Below this, the statistic is suppressed (<see langword="null"/>) rather than
    /// computed and shown, since a correlation from too few points is noise dressed as precision. A starting
    /// guess (30), not a measured threshold - revisit once real <c>grader_scores</c> volume is visible.
    /// </summary>
    int MinimumSampleSize { get; }

    /// <summary>Computes the full report over every row currently in <c>grader_scores</c>.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<GraderReliabilityReport> AnalyzeAsync(CancellationToken cancellationToken = default);
}
