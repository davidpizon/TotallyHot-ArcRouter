namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Produces a <see cref="SyntaxVerdict"/> for a snippet without executing it (Tier 0 static analysis).
/// </summary>
public interface IStructuralParser
{
    /// <summary>Checks the structural validity of <paramref name="code"/> in the given language.</summary>
    /// <param name="code">The source code to check.</param>
    /// <param name="language">The language to check it as.</param>
    /// <returns>The validity verdict.</returns>
    SyntaxVerdict Check(string code, CodeLanguage language);
}

