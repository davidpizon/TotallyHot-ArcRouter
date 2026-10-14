namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// The outcome of a structural (parse/compile) check on an extracted snippet, produced independently of
/// execution so a snippet that cannot run still yields a validity signal.
/// </summary>
/// <param name="Language">The language the snippet was checked as.</param>
/// <param name="IsValid"><see langword="true"/> when the snippet parsed/compiled cleanly.</param>
/// <param name="IsAuthoritative">
/// <see langword="true"/> when the check is a real parser (e.g. Roslyn for C#); <see langword="false"/>
/// when it is only a lightweight in-process heuristic and the authoritative check happens in a higher tier.
/// </param>
/// <param name="Confidence">
/// How sure the checker is, in <c>[0, 1]</c>. Authoritative parsers report <c>1.0</c>; language-aware
/// heuristics report less. Below <see cref="Grading.GradeStatusCalculator.MinimumSyntaxConfidenceForComplete"/>
/// the grade cannot be <see cref="GradeStatus.Complete"/> (issue #293 D3).
/// </param>
/// <param name="Errors">Human-readable diagnostics when <paramref name="IsValid"/> is <see langword="false"/>.</param>
public sealed record SyntaxVerdict(
    CodeLanguage Language,
    bool IsValid,
    bool IsAuthoritative,
    double Confidence,
    IReadOnlyList<string> Errors)
{
    /// <summary>Confidence for a real in-process parser (Roslyn, Acornima).</summary>
    public const double AuthoritativeConfidence = 1.0;

    /// <summary>Confidence for the language-aware Python / shell structural scanners.</summary>
    public const double LanguageAwareHeuristicConfidence = 0.75;

    /// <summary>Confidence for the generic delimiter-balance heuristic.</summary>
    public const double GenericHeuristicConfidence = 0.55;

    /// <summary>Creates a valid verdict with no diagnostics.</summary>
    /// <param name="language">The language that was checked.</param>
    /// <param name="isAuthoritative">Whether a real parser produced this verdict.</param>
    /// <param name="confidence">How sure the checker is, in <c>[0, 1]</c>.</param>
    /// <returns>A valid <see cref="SyntaxVerdict"/>.</returns>
    public static SyntaxVerdict Valid(CodeLanguage language, bool isAuthoritative, double confidence)
    {
        return new SyntaxVerdict(Language: language, true, IsAuthoritative: isAuthoritative,
            Confidence: confidence, Errors: []);
    }

    /// <summary>Creates an invalid verdict carrying the supplied diagnostics.</summary>
    /// <param name="language">The language that was checked.</param>
    /// <param name="isAuthoritative">Whether a real parser produced this verdict.</param>
    /// <param name="confidence">How sure the checker is, in <c>[0, 1]</c>.</param>
    /// <param name="errors">The diagnostics explaining why the snippet is invalid.</param>
    /// <returns>An invalid <see cref="SyntaxVerdict"/>.</returns>
    public static SyntaxVerdict Invalid(
        CodeLanguage language,
        bool isAuthoritative,
        double confidence,
        IReadOnlyList<string> errors)
    {
        return new SyntaxVerdict(Language: language, false, IsAuthoritative: isAuthoritative,
            Confidence: confidence, Errors: errors);
    }
}