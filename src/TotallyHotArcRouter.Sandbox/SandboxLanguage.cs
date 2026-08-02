namespace TotallyHot.ArcRouter.Sandbox;

/// <summary>
/// A source language the sandbox can produce a validity or execution signal for.
/// </summary>
public enum SandboxLanguage
{
    /// <summary>An unrecognized or unsupported language; only a trivial structural check applies.</summary>
    Unknown,

    /// <summary>C#. Validity comes from an in-process Roslyn parse (Tier 0).</summary>
    CSharp,

    /// <summary>Python 3.</summary>
    Python,

    /// <summary>JavaScript (Node.js runtime).</summary>
    JavaScript,

    /// <summary>POSIX shell.</summary>
    Shell,
}

/// <summary>
/// Helpers for mapping fenced-code-block language hints onto <see cref="SandboxLanguage"/> and
/// classifying which languages are executable.
/// </summary>
public static class SandboxLanguages
{
    /// <summary>
    /// Maps a Markdown fence language hint (e.g. <c>py</c>, <c>js</c>, <c>bash</c>) to a <see cref="SandboxLanguage"/>.
    /// </summary>
    /// <param name="hint">The raw language token from a code fence; may be <see langword="null"/> or empty.</param>
    /// <returns>The matched language, or <see cref="SandboxLanguage.Unknown"/> when unrecognized.</returns>
    public static SandboxLanguage FromHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return SandboxLanguage.Unknown;
        }

        return hint.Trim().ToLowerInvariant() switch
        {
            "c#" or "cs" or "csharp" or "dotnet" => SandboxLanguage.CSharp,
            "py" or "python" or "python3" => SandboxLanguage.Python,
            "js" or "javascript" or "node" or "mjs" or "cjs" => SandboxLanguage.JavaScript,
            // TypeScript is parsed with the JS probe: close enough for a structural validity signal.
            "ts" or "typescript" => SandboxLanguage.JavaScript,
            "sh" or "shell" or "bash" or "zsh" or "console" => SandboxLanguage.Shell,
            _ => SandboxLanguage.Unknown,
        };
    }

    /// <summary>
    /// Indicates whether a language can, in principle, be executed by the Tier 1/Tier 2 runtimes (as
    /// opposed to receiving only a Tier-0 syntax signal, as C# does at launch).
    /// </summary>
    /// <param name="language">The language to test.</param>
    /// <returns><see langword="true"/> for Python/JavaScript/Shell; otherwise <see langword="false"/>.</returns>
    public static bool IsExecutable(SandboxLanguage language) =>
        language is SandboxLanguage.Python or SandboxLanguage.JavaScript or SandboxLanguage.Shell;
}

