using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TotallyHot.ArcRouter.Tools;

/// <summary>
/// A tool to check the syntax of a C# code snippet.
/// </summary>
public class CheckSyntax
{
    /// <summary>
    /// Checks the syntax of the provided C# code.
    /// </summary>
    /// <param name="code">The C# code to check.</param>
    /// <returns>A collection of diagnostics if syntax errors are found; otherwise, an empty collection.</returns>
    public IEnumerable<Diagnostic> Check(string code)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);
        var diagnostics = syntaxTree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        return diagnostics;
    }
}