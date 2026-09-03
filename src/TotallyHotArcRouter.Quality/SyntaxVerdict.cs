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
/// <param name="Errors">Human-readable diagnostics when <paramref name="IsValid"/> is <see langword="false"/>.</param>
public sealed record SyntaxVerdict(
    CodeLanguage Language,
    bool IsValid,
    bool IsAuthoritative,
    IReadOnlyList<string> Errors)
{
    /// <summary>Creates a valid verdict with no diagnostics.</summary>
    /// <param name="language">The language that was checked.</param>
    /// <param name="isAuthoritative">Whether a real parser produced this verdict.</param>
    /// <returns>A valid <see cref="SyntaxVerdict"/>.</returns>
    public static SyntaxVerdict Valid(CodeLanguage language, bool isAuthoritative)
    {
        return new SyntaxVerdict(Language: language, true, IsAuthoritative: isAuthoritative,
            Errors: Array.Empty<string>());
    }

    /// <summary>Creates an invalid verdict carrying the supplied diagnostics.</summary>
    /// <param name="language">The language that was checked.</param>
    /// <param name="isAuthoritative">Whether a real parser produced this verdict.</param>
    /// <param name="errors">The diagnostics explaining why the snippet is invalid.</param>
    /// <returns>An invalid <see cref="SyntaxVerdict"/>.</returns>
    public static SyntaxVerdict Invalid(CodeLanguage language, bool isAuthoritative, IReadOnlyList<string> errors)
    {
        return new SyntaxVerdict(Language: language, false, IsAuthoritative: isAuthoritative, Errors: errors);
    }
}