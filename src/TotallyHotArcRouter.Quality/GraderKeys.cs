namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Well-known keys identifying the graders that can hold a request open on
/// <see cref="Grading.IQualityScoreAggregator"/> or contribute an extra axis to
/// <see cref="IQualityScorer"/>'s blend. The three built-in graders (syntax, analysis, judge) have their own
/// named fields on <see cref="QualityResult"/> for backward compatibility; these constants exist so the one
/// grader that is asynchronous today - the judge - is addressed by the same string in the aggregator's
/// pending-grader set as any future asynchronous grader (Phase Q3) will be, rather than by an implicit
/// single-slot flag.
/// </summary>
public static class GraderKeys
{
    /// <summary>The structural-parse grader (<see cref="QualityResult.SyntaxValid"/>/<see cref="QualityResult.SyntaxAuthoritative"/>).</summary>
    public const string Syntax = "syntax";

    /// <summary>The composed static-analysis grader (<see cref="QualityResult.AnalysisScore"/>).</summary>
    public const string Analysis = "analysis";

    /// <summary>The G-Eval judge grader (<see cref="QualityResult.JudgeScore"/>).</summary>
    public const string Judge = "judge";
}
