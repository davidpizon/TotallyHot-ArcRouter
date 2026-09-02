namespace TotallyHot.ArcRouter.Quality.Analysis;

/// <summary>
/// One in-process static check over an extracted snippet. Analyzers are composed rather than hard-coded
/// into the scorer, so adding a new signal is a registration rather than an edit to the scoring maths.
/// </summary>
/// <remarks>
/// An analyzer reads the code. It never runs it, never spawns a process, and never touches the network -
/// those are the terms on which this assembly is allowed to look at model output at all.
/// </remarks>
public interface IStaticAnalyzer
{
    /// <summary>A short stable identifier for this analyzer, used to attribute its findings in telemetry.</summary>
    string Name { get; }

    /// <summary>Analyzes a snippet.</summary>
    /// <param name="code">The source code to analyze.</param>
    /// <param name="language">The detected language of <paramref name="code"/>.</param>
    /// <returns>
    /// The finding, or <see langword="null"/> when this analyzer has nothing to say about this snippet
    /// (wrong language, or no evidence either way). Null is an abstention: the composite drops it from the
    /// mean rather than treating silence as a zero.
    /// </returns>
    StaticAnalysisFinding? Analyze(string code, CodeLanguage language);
}

/// <summary>One analyzer's verdict on a snippet.</summary>
/// <param name="Analyzer">The <see cref="IStaticAnalyzer.Name"/> that produced this finding.</param>
/// <param name="Score">
/// The analyzer's score in [0,1], where 1 is clean and 0 is as bad as this analyzer can report. Values
/// outside the range are clamped by the composite.
/// </param>
/// <param name="Notes">Human-readable evidence for the score, retained for telemetry and diagnostics.</param>
public sealed record StaticAnalysisFinding(string Analyzer, double Score, IReadOnlyList<string> Notes);

/// <summary>The composed outcome of running every registered analyzer over one snippet.</summary>
/// <param name="Score">
/// The mean of the applicable findings' scores, or <see langword="null"/> when every analyzer abstained -
/// in which case the scorer drops the analysis axis rather than scoring the snippet zero for the
/// harness's own lack of an opinion.
/// </param>
/// <param name="Notes">Every contributing analyzer's notes, flattened and prefixed with the analyzer name.</param>
public sealed record StaticAnalysisReport(double? Score, IReadOnlyList<string> Notes);

/// <summary>
/// Shared scoring arithmetic for <see cref="IStaticAnalyzer"/> implementations that grade a snippet by
/// deducting a per-occurrence penalty from a perfect score, floored so no single axis can reach zero on
/// its own.
/// </summary>
/// <remarks>
/// Every analyzer in this namespace that scores by penalty independently reimplemented
/// <c>Math.Max(floor, 1.0 - penalty)</c> with its own floor and its own penalty computation. This factors
/// out the one line all of them share; each analyzer still owns its own floor constant and the arithmetic
/// that turns its findings into a penalty.
/// </remarks>
public static class StaticAnalyzerScoring
{
    /// <summary>
    /// Deducts <paramref name="penalty"/> from a perfect score of 1.0, clamped so the result never drops
    /// below <paramref name="floor"/>.
    /// </summary>
    /// <param name="floor">The lowest score this call may return.</param>
    /// <param name="penalty">The total deduction to apply before flooring, computed by the caller.</param>
    /// <returns>The floored score.</returns>
    public static double ClampScore(double floor, double penalty) => Math.Max(floor, 1.0 - penalty);
}
