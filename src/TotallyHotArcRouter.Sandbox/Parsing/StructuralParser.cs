using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TotallyHot.ArcRouter.Sandbox.Parsing;

/// <summary>
/// Tier-0 structural parser. C# is parsed authoritatively with Roslyn; Python/JavaScript/Shell receive a
/// cheap in-process delimiter-balance heuristic (their authoritative syntax check is a Tier-1 subprocess).
/// </summary>
public sealed class StructuralParser : IStructuralParser
{
    /// <inheritdoc />
    public SyntaxVerdict Check(string code, SandboxLanguage language)
    {
        ArgumentNullException.ThrowIfNull(code);

        return language switch
        {
            SandboxLanguage.CSharp => CheckCSharp(code),
            SandboxLanguage.Python or SandboxLanguage.JavaScript or SandboxLanguage.Shell =>
                CheckHeuristic(code, language),
            _ => CheckHeuristic(code, SandboxLanguage.Unknown),
        };
    }

    /// <summary>Authoritatively checks C# syntax by parsing it with Roslyn and collecting any error-severity diagnostics.</summary>
    private static SyntaxVerdict CheckCSharp(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        return errors.Count == 0
            ? SyntaxVerdict.Valid(SandboxLanguage.CSharp, isAuthoritative: true)
            : SyntaxVerdict.Invalid(SandboxLanguage.CSharp, isAuthoritative: true, errors);
    }

    /// <summary>Non-authoritatively checks a non-C# language by rejecting empty snippets and otherwise delegating to the delimiter-balance heuristic.</summary>
    private static SyntaxVerdict CheckHeuristic(string code, SandboxLanguage language)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return SyntaxVerdict.Invalid(language, isAuthoritative: false, ["Snippet is empty."]);
        }

        return DelimiterBalance.IsBalanced(code, out var error)
            ? SyntaxVerdict.Valid(language, isAuthoritative: false)
            : SyntaxVerdict.Invalid(language, isAuthoritative: false, [error!]);
    }
}

