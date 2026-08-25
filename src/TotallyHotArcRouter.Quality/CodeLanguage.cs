namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// A source language the quality verifier can produce a validity signal for.
/// </summary>
public enum CodeLanguage
{
    /// <summary>An unrecognized or unsupported language; only a trivial structural check applies.</summary>
    Unknown,

    /// <summary>C#. Validity comes from an authoritative in-process Roslyn parse.</summary>
    CSharp,

    /// <summary>Python 3. Checked heuristically - no in-process Python parser is available to .NET.</summary>
    Python,

    /// <summary>JavaScript (and TypeScript, checked as JavaScript).</summary>
    JavaScript,

    /// <summary>POSIX shell. Checked heuristically.</summary>
    Shell,
}

/// <summary>
/// Helpers for mapping fenced-code-block language hints onto <see cref="CodeLanguage"/> and reporting
/// which of them a real parser backs.
/// </summary>
public static class CodeLanguages
{
    /// <summary>
    /// Maps a Markdown fence language hint (e.g. <c>py</c>, <c>js</c>, <c>bash</c>) to a <see cref="CodeLanguage"/>.
    /// </summary>
    /// <param name="hint">The raw language token from a code fence; may be <see langword="null"/> or empty.</param>
    /// <returns>The matched language, or <see cref="CodeLanguage.Unknown"/> when unrecognized.</returns>
    public static CodeLanguage FromHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return CodeLanguage.Unknown;
        }

        return hint.Trim().ToLowerInvariant() switch
        {
            "c#" or "cs" or "csharp" or "dotnet" => CodeLanguage.CSharp,
            "py" or "python" or "python3" => CodeLanguage.Python,
            "js" or "javascript" or "node" or "mjs" or "cjs" => CodeLanguage.JavaScript,
            // TypeScript is parsed as JavaScript: close enough for a structural validity signal, and the
            // JS parser tolerates most type annotations it meets in practice.
            "ts" or "typescript" => CodeLanguage.JavaScript,
            "sh" or "shell" or "bash" or "zsh" or "console" => CodeLanguage.Shell,
            _ => CodeLanguage.Unknown,
        };
    }

    /// <summary>
    /// Indicates whether a real parser backs this language's syntax verdict, as opposed to the
    /// delimiter-balance heuristic. Callers use it to decide how much weight a validity signal deserves;
    /// it is also what <see cref="QualityResult.SyntaxAuthoritative"/> ends up reporting.
    /// </summary>
    /// <param name="language">The language to test.</param>
    /// <returns><see langword="true"/> for C# and JavaScript; otherwise <see langword="false"/>.</returns>
    public static bool HasAuthoritativeParser(CodeLanguage language) =>
        language is CodeLanguage.CSharp or CodeLanguage.JavaScript;
}

