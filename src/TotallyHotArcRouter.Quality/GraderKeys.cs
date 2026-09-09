namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Well-known keys identifying the graders that can hold a request open on
/// <see cref="Grading.IQualityScoreAggregator"/> or contribute an extra axis to
/// <see cref="IQualityScorer"/>'s blend. The three built-in graders (syntax, analysis, judge) have their own
/// named fields on <see cref="QualityResult"/> for backward compatibility; these constants exist so every
/// asynchronous grader - the judge, and Phase Q3's CodeJudge/ICE-Score/RACE portfolio - is addressed by the
/// same string in the aggregator's pending-grader set, rather than by an implicit single-slot flag.
/// </summary>
public static class GraderKeys
{
    /// <summary>The structural-parse grader (<see cref="QualityResult.SyntaxValid"/>/<see cref="QualityResult.SyntaxAuthoritative"/>).</summary>
    public const string Syntax = "syntax";

    /// <summary>The composed static-analysis grader (<see cref="QualityResult.AnalysisScore"/>).</summary>
    public const string Analysis = "analysis";

    /// <summary>The G-Eval judge grader (<see cref="QualityResult.JudgeScore"/>).</summary>
    public const string Judge = "judge";

    /// <summary>
    /// The CodeJudge correctness grader (Phase Q3; Tong &amp; Zhang, EMNLP 2024), contributing to
    /// <see cref="QualityResult.GraderScores"/>.
    /// </summary>
    public const string CodeJudge = "codejudge";

    /// <summary>
    /// The ICE-Score usefulness grader (Phase Q3; Zhuo, Findings of EACL 2024), contributing to
    /// <see cref="QualityResult.GraderScores"/>.
    /// </summary>
    public const string IceScore = "icescore";

    /// <summary>
    /// The RACE readability/maintainability grader (Phase Q3; Zheng et al.), contributing to
    /// <see cref="QualityResult.GraderScores"/>.
    /// </summary>
    public const string Race = "race";
}
