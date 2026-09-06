using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.Quality.Analysis;

/// <summary>
/// Scores a snippet by Szych &amp; Schwerk's density formula
/// (docs/research/code-quality-metrics-assessment.md §5.1): <c>(findings / linesOfCode) * 100</c>, over a
/// small, self-contained smell catalog independent of every other analyzer's signal.
/// </summary>
/// <remarks>
/// <para>
/// <b>The catalog.</b> The paper supplies the ratio, not a smell list, so this counts four smells chosen
/// for being cheap to detect from text alone and structurally different from what the other three
/// analyzers already report (diagnostics, placeholders/truncation, nesting/branch density): magic numbers,
/// overlong lines, empty <c>catch</c>/<c>except</c> blocks that silently swallow an error, and parameter
/// lists long enough to suggest a signature doing too much.
/// </para>
/// <para>
/// <b>A mild band, not a hard axis</b>, exactly like <see cref="ComplexityAnalyzer"/>: density at or below
/// <see cref="DensityBudget"/> findings per 100 lines scores 1.0, density scales down to
/// <see cref="Floor"/> by <see cref="DensityCeiling"/>, and never drops below the floor - this is a nudge,
/// not a verdict that can zero a snippet on its own.
/// </para>
/// </remarks>
public sealed partial class SmellDensityAnalyzer : IStaticAnalyzer
{
    /// <summary>Findings-per-100-lines density at or below which no penalty applies at all.</summary>
    private const double DensityBudget = 2.0;

    /// <summary>Findings-per-100-lines density at or above which the score bottoms out at <see cref="Floor"/>.</summary>
    private const double DensityCeiling = 20.0;

    /// <summary>The lowest score this analyzer will report, keeping it a nudge rather than a verdict.</summary>
    private const double Floor = 0.3;

    /// <summary>The longest line, in characters, not counted as a smell.</summary>
    private const int LongLineLength = 120;

    /// <summary>The fewest non-blank lines a snippet needs before density is worth scoring at all.</summary>
    private const int MinimumLines = 3;

    /// <summary>Comma count within one parenthesized group at or above which it counts as a long parameter list.</summary>
    private const int LongParameterListCommas = 5;

    /// <inheritdoc/>
    public string Name => "smell_density";

    /// <inheritdoc/>
    public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(code);

        var lines = code.Replace(oldValue: "\r\n", newValue: "\n").Split('\n');
        var nonBlankLines = lines.Count(l => !string.IsNullOrWhiteSpace(l));

        // A snippet this short has no meaningful density to score; abstain rather than manufacture an
        // opinion from one or two lines.
        if (nonBlankLines < MinimumLines) return null;

        var notes = new List<string>();
        var smellCount = 0;

        smellCount += CountMagicNumbers(lines: lines, notes: notes);
        smellCount += CountLongLines(lines: lines, notes: notes);
        smellCount += CountEmptyCatchBlocks(code: code, notes: notes);
        smellCount += CountLongParameterLists(lines: lines, notes: notes);

        var density = smellCount / (double)nonBlankLines * 100.0;
        var score = ScoreFromDensity(density);

        return new StaticAnalysisFinding(Analyzer: Name, Score: score, Notes: notes);
    }

    /// <summary>Maps a findings-per-100-lines density onto [Floor, 1.0] via the budget/ceiling band.</summary>
    private static double ScoreFromDensity(double density)
    {
        if (density <= DensityBudget) return 1.0;
        if (density >= DensityCeiling) return Floor;

        var fraction = (density - DensityBudget) / (DensityCeiling - DensityBudget);
        return 1.0 - fraction * (1.0 - Floor);
    }

    /// <summary>
    /// Counts numeric literals other than 0, 1, and -1 on lines that do not look like a constant
    /// declaration - an approximate signal (there is no compiler here to tell a magic number from a named
    /// constant's own initializer), consistent with this assembly's other text-only heuristics.
    /// </summary>
    private static int CountMagicNumbers(IReadOnlyList<string> lines, List<string> notes)
    {
        var count = 0;

        foreach (var line in lines)
        {
            if (DeclarationLine().IsMatch(line)) continue;

            foreach (Match match in NumericLiteral().Matches(line))
                if (!IsExemptNumber(match.Value))
                    count++;
        }

        if (count > 0) notes.Add($"{count} x magic number");
        return count;
    }

    private static bool IsExemptNumber(string literal)
    {
        return literal is "0" or "1" or "-1" or "0.0" or "1.0" or "-1.0";
    }

    /// <summary>Counts lines longer than <see cref="LongLineLength"/> characters.</summary>
    private static int CountLongLines(IReadOnlyList<string> lines, List<string> notes)
    {
        var count = lines.Count(l => l.Length > LongLineLength);
        if (count > 0) notes.Add($"{count} x line over {LongLineLength} characters");
        return count;
    }

    /// <summary>
    /// Counts <c>catch</c>/<c>except</c> blocks with nothing but whitespace (or Python's bare <c>pass</c>)
    /// in their body - the pattern of an error swallowed rather than handled.
    /// </summary>
    private static int CountEmptyCatchBlocks(string code, List<string> notes)
    {
        var count = EmptyCatchBlock().Matches(code).Count + EmptyExceptBlock().Matches(code).Count;
        if (count > 0) notes.Add($"{count} x empty catch/except block");
        return count;
    }

    /// <summary>
    /// Counts single-line parenthesized groups carrying at least <see cref="LongParameterListCommas"/>
    /// commas - a text-only proxy for "this signature (or call) takes too many arguments" that does not
    /// try to distinguish a declaration from a call, matching this analyzer's approximate-by-design posture.
    /// </summary>
    private static int CountLongParameterLists(IReadOnlyList<string> lines, List<string> notes)
    {
        var count = 0;

        foreach (var line in lines)
        foreach (Match match in ParenGroup().Matches(line))
            if (match.Groups[1].Value.Count(c => c == ',') >= LongParameterListCommas)
                count++;

        if (count > 0) notes.Add($"{count} x parameter/argument list with {LongParameterListCommas}+ items");
        return count;
    }

    [GeneratedRegex(pattern: @"\b(const|readonly|static\s+final|final|enum)\b", options: RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DeclarationLine();

    [GeneratedRegex(pattern: @"(?<![\w.])-?\d+(\.\d+)?(?![\w.])", options: RegexOptions.None, 1000)]
    private static partial Regex NumericLiteral();

    [GeneratedRegex(pattern: @"catch\s*(\([^)]*\))?\s*\{\s*\}", options: RegexOptions.None, 1000)]
    private static partial Regex EmptyCatchBlock();

    [GeneratedRegex(pattern: @"except[^:\n]*:\s*\n?\s*pass\b", options: RegexOptions.None, 1000)]
    private static partial Regex EmptyExceptBlock();

    [GeneratedRegex(pattern: @"\(([^()]*)\)", options: RegexOptions.None, 1000)]
    private static partial Regex ParenGroup();
}
