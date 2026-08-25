namespace TotallyHot.ArcRouter.Quality.Analysis;

/// <summary>
/// Scores a snippet's shape: how deeply it nests and how many branches it carries per line.
/// </summary>
/// <remarks>
/// <b>A mild band, not a hard axis.</b> Complexity is the weakest of the static signals and the easiest to
/// misread - a genuinely hard algorithm is supposed to branch, and penalizing it for doing so would teach
/// the router to prefer models that dodge hard problems. So this reports 1.0 across the whole range a
/// reasonable answer occupies and only tapers past thresholds that indicate an answer nobody would want to
/// maintain, with a floor of 0.5 so it can never dominate a score on its own.
/// <para>
/// Nesting is measured from leading indentation rather than by parsing, which keeps it language-agnostic
/// and costs nothing; the parser that already ran is not re-entered just to count braces.
/// </para>
/// </remarks>
public sealed class ComplexityAnalyzer : IStaticAnalyzer
{
    /// <summary>Indentation depth (in levels) below which no penalty applies at all.</summary>
    private const int NestingBudget = 4;

    /// <summary>Branch-keywords-per-line ratio below which no penalty applies at all.</summary>
    private const double BranchDensityBudget = 0.25;

    /// <summary>The lowest score this analyzer will report, keeping it a nudge rather than a verdict.</summary>
    private const double Floor = 0.5;

    /// <summary>The fewest non-blank lines a snippet needs before its shape is worth scoring at all.</summary>
    private const int MinimumLines = 5;

    /// <summary>Tokens counted as branches, common across every language this verifier sees.</summary>
    private static readonly string[] BranchKeywords =
        ["if", "else", "elif", "for", "while", "case", "catch", "except", "switch", "&&", "||"];

    /// <inheritdoc />
    public string Name => "complexity";

    /// <inheritdoc />
    public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(code);

        var lines = code
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // A snippet this short has no shape worth scoring; abstain rather than manufacture an opinion.
        if (lines.Count < MinimumLines)
        {
            return null;
        }

        var indentUnit = InferIndentUnit(lines);
        var maxDepth = lines.Max(l => (l.Length - l.TrimStart(' ').Length) / indentUnit);

        var branches = lines.Sum(CountBranches);
        var density = (double)branches / lines.Count;

        var notes = new List<string>();
        var penalty = 0.0;

        if (maxDepth > NestingBudget)
        {
            penalty += (maxDepth - NestingBudget) * 0.1;
            notes.Add($"nesting depth {maxDepth} exceeds a budget of {NestingBudget}");
        }

        if (density > BranchDensityBudget)
        {
            penalty += (density - BranchDensityBudget) * 0.8;
            notes.Add($"branch density {density:F2} per line exceeds a budget of {BranchDensityBudget:F2}");
        }

        return new StaticAnalysisFinding(Name, Math.Max(Floor, 1.0 - penalty), notes);
    }

    /// <summary>
    /// Infers the snippet's indentation unit from the smallest non-zero leading-space run, so a
    /// two-space file is not read as half the depth of a four-space one. Falls back to 4.
    /// </summary>
    private static int InferIndentUnit(IReadOnlyList<string> lines)
    {
        var indents = lines
            .Select(l => l.Length - l.TrimStart(' ').Length)
            .Where(i => i > 0)
            .ToList();

        return indents.Count == 0 ? 4 : Math.Max(1, indents.Min());
    }

    /// <summary>Counts branch keywords on one line, matching whole words so <c>ifconfig</c> is not a branch.</summary>
    private static int CountBranches(string line)
    {
        var count = 0;
        foreach (var keyword in BranchKeywords)
        {
            var index = 0;
            while ((index = line.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
            {
                var beforeOk = index == 0 || !char.IsLetterOrDigit(line[index - 1]);
                var after = index + keyword.Length;
                var afterOk = after >= line.Length || !char.IsLetterOrDigit(line[after]);

                if (beforeOk && afterOk)
                {
                    count++;
                }

                index = after;
            }
        }

        return count;
    }
}
