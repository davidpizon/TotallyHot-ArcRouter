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
    /// <remarks>
    /// Reported by <c>UnusedMemberInSuper.Global</c>: every call reaches this through the implementing type
    /// rather than this interface. It is not dead - see the declaration's callers. Narrowing the interface
    /// to match today's call sites is a design change, not a scan fix (ADR-0008's stop rules).
    /// </remarks>
    // ReSharper disable once UnusedMemberInSuper.Global
    int MinimumSampleSize { get; }

    /// <summary>Computes the full report over every row currently in <c>grader_scores</c>.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<GraderReliabilityReport> AnalyzeAsync(CancellationToken cancellationToken = default);
}
