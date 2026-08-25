using Acornima;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TotallyHot.ArcRouter.Quality.Parsing;

/// <summary>
/// Produces a snippet's syntax verdict entirely in process, by parsing it. C# is parsed authoritatively
/// with Roslyn and JavaScript/TypeScript with Acornima; Python and shell fall back to a delimiter-balance
/// heuristic and say so, because no pure managed parser exists for them.
/// </summary>
/// <remarks>
/// <b>Parsing only, never evaluation.</b> Both parsers here build a syntax tree and stop - neither has an
/// evaluator attached, and neither package ships one (Acornima is Jint's parser front-end, taken without
/// Jint's interpreter, precisely so this assembly has no way to run what it reads). That is a structural
/// guarantee rather than a policy one: there is no code path from a <see cref="SyntaxVerdict"/> to a
/// running program, and no subprocess is spawned to obtain one.
/// <para>
/// A language without an authoritative parser is <em>marked</em>, not silently downgraded:
/// <see cref="SyntaxVerdict.IsAuthoritative"/> is false and the scorer weighs the signal accordingly. The
/// alternative - letting a bracket-counting heuristic pass for a compiler's verdict - would quietly
/// inflate every Python score the router learns from.
/// </para>
/// </remarks>
public sealed class StructuralParser : IStructuralParser
{
    /// <inheritdoc />
    public SyntaxVerdict Check(string code, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(code);

        return language switch
        {
            CodeLanguage.CSharp => CheckCSharp(code),
            CodeLanguage.JavaScript => CheckJavaScript(code),
            CodeLanguage.Python or CodeLanguage.Shell => CheckHeuristic(code, language),
            _ => CheckHeuristic(code, CodeLanguage.Unknown),
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
            ? SyntaxVerdict.Valid(CodeLanguage.CSharp, isAuthoritative: true)
            : SyntaxVerdict.Invalid(CodeLanguage.CSharp, isAuthoritative: true, errors);
    }

    /// <summary>
    /// Authoritatively checks JavaScript (and TypeScript, which reaches here via
    /// <see cref="CodeLanguages.FromHint"/>) by parsing it with Acornima.
    /// </summary>
    /// <remarks>
    /// Parsed as a module first, then retried as a script. A model's answer is as likely to be a bare
    /// statement sequence as an ES module, and the two grammars disagree about top-level <c>await</c>,
    /// <c>import</c>, and implicit strict mode - trying only one would fail perfectly good code on a
    /// technicality. Only a snippet both grammars reject is reported invalid.
    /// </remarks>
    private static SyntaxVerdict CheckJavaScript(string code)
    {
        var parser = new Parser();

        try
        {
            parser.ParseModule(code);
            return SyntaxVerdict.Valid(CodeLanguage.JavaScript, isAuthoritative: true);
        }
        catch (ParseErrorException moduleError)
        {
            try
            {
                parser.ParseScript(code);
                return SyntaxVerdict.Valid(CodeLanguage.JavaScript, isAuthoritative: true);
            }
            catch (ParseErrorException scriptError)
            {
                // Report the script-grammar message: it is the more permissive of the two, so its
                // complaint is the one describing a genuine syntax problem rather than a module-only rule.
                return SyntaxVerdict.Invalid(
                    CodeLanguage.JavaScript,
                    isAuthoritative: true,
                    [scriptError.Message, moduleError.Message]);
            }
        }
    }

    /// <summary>Non-authoritatively checks a language with no managed parser by rejecting empty snippets and otherwise delegating to the delimiter-balance heuristic.</summary>
    private static SyntaxVerdict CheckHeuristic(string code, CodeLanguage language)
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
