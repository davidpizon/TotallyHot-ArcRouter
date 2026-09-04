using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Globalization;

namespace TotallyHot.ArcRouter.Quality.Analysis;

/// <summary>
/// Grades a C# snippet on the warning-severity diagnostics Roslyn reports, below the error threshold that
/// the syntax axis already covers.
/// </summary>
/// <remarks>
/// Syntax validity is a single bit, and a great deal of quality difference lives underneath it: a snippet
/// that parses but trips a dozen warnings is not the same answer as one that parses clean. Roslyn hands
/// these over for free during the parse the structural check already performs, so this axis costs one
/// extra tree walk and no new dependency.
/// <para>
/// <b>Parse-level diagnostics only.</b> No <see cref="CSharpCompilation"/> is created and no references
/// are resolved, so this never reports "type or namespace not found" for a snippet whose imports simply
/// were not pasted along with it - a complaint that would say more about the extraction than the model.
/// The trade is that genuinely semantic problems go unseen; that is the judge's half of the job.
/// </para>
/// </remarks>
public sealed class DiagnosticSeverityAnalyzer : IStaticAnalyzer
{
    /// <summary>Penalty deducted per warning-severity diagnostic.</summary>
    private const double PenaltyPerWarning = 0.1;

    /// <summary>The lowest score this analyzer will report, keeping warnings a markdown rather than a zero.</summary>
    private const double Floor = 0.2;

    /// <summary>The most individual diagnostics quoted in the notes, so telemetry stays bounded.</summary>
    private const int MaxQuotedDiagnostics = 5;

    /// <inheritdoc/>
    public string Name => "diagnostics";

    /// <inheritdoc/>
    public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(code);

        // Roslyn is the only compiler front-end in this assembly, so C# is the only language with
        // diagnostics to mine. Every other language abstains rather than being scored on nothing.
        if (language != CodeLanguage.CSharp || string.IsNullOrWhiteSpace(code)) return null;

        var tree = CSharpSyntaxTree.ParseText(code);
        var warnings = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Warning)
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture))
            .ToList();

        if (warnings.Count == 0) return new StaticAnalysisFinding(Analyzer: Name, 1.0, Notes: []);

        var notes = new List<string> { $"{warnings.Count} parse warning(s)" };
        notes.AddRange(warnings.Take(MaxQuotedDiagnostics));

        var score = StaticAnalyzerScoring.ClampScore(floor: Floor, penalty: PenaltyPerWarning * warnings.Count);
        return new StaticAnalysisFinding(Analyzer: Name, Score: score, Notes: notes);
    }
}