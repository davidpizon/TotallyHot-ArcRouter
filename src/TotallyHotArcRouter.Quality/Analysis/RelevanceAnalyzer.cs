using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.Quality.Analysis;

/// <summary>
/// Scores whether a snippet plausibly addresses the prompt it was written to answer, following CodeAgent's
/// QA-Checker relevance/drift idea (docs/research/code-quality-metrics-assessment.md §5.1): a complete,
/// warning-free answer to a <em>different</em> question should not score the same as one that answers this
/// one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The heuristic.</b> This has no reference implementation and no test suite to compare against - it is
/// a token-overlap proxy, not a semantic check. Salient words are extracted from the prompt (a fixed
/// stop-word list removed, short tokens dropped) and checked for whole-word presence anywhere in the code
/// (identifiers, literals, and comments alike, since the code is scanned as text rather than parsed). The
/// fraction that appears is the relevance signal.
/// </para>
/// <para>
/// <b>Abstains rather than penalizing a terse prompt.</b> A prompt that yields fewer than
/// <see cref="MinimumSalientTokens"/> salient words carries too little signal to accuse a response of
/// drifting from it - "fix this" and "write a function" are common, short, and legitimately vague. This
/// mirrors every other analyzer's "no evidence either way" abstention rather than manufacturing an opinion.
/// </para>
/// <para>
/// <b>A mild band, not a hard axis</b>, exactly like <see cref="ComplexityAnalyzer"/>: full overlap is not
/// required for a perfect score (a correct answer often uses synonyms or different casing/tense than the
/// prompt), and the floor keeps a low-overlap verdict from dominating the blend on its own.
/// </para>
/// </remarks>
public sealed partial class RelevanceAnalyzer : IStaticAnalyzer
{
    /// <summary>Overlap fraction at or above which this analyzer reports a perfect score.</summary>
    private const double OverlapBudget = 0.5;

    /// <summary>The lowest score this analyzer will report, keeping it a nudge rather than a verdict.</summary>
    private const double Floor = 0.3;

    /// <summary>The fewest salient prompt tokens needed before drift is worth scoring at all.</summary>
    private const int MinimumSalientTokens = 3;

    /// <summary>
    /// Common words and instruction verbs that carry no topical signal about the task, so they are dropped
    /// before computing overlap rather than trivially "matching" almost any snippet.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "to", "of", "in", "on", "for", "with", "that", "this", "these",
        "those", "is", "are", "was", "were", "be", "been", "it", "its", "as", "at", "by", "from", "into",
        "please", "can", "could", "you", "your", "write", "create", "implement", "add", "make", "fix", "update",
        "using", "use", "should", "would", "need", "needs", "want", "wants", "code", "function", "method",
        "class", "just", "also", "will", "than", "then", "have", "has", "had", "not", "any", "all", "some"
    };

    /// <inheritdoc/>
    public string Name => "relevance";

    /// <inheritdoc/>
    public StaticAnalysisFinding? Analyze(string code, CodeLanguage language)
    {
        return Analyze(code: code, language: language, prompt: string.Empty);
    }

    /// <inheritdoc/>
    public StaticAnalysisFinding? Analyze(string code, CodeLanguage language, string prompt)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(prompt);

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(prompt)) return null;

        var promptTokens = ExtractSalientTokens(prompt);
        if (promptTokens.Count < MinimumSalientTokens) return null;

        var codeTokens = ExtractSalientTokens(code);
        var matched = promptTokens.Count(codeTokens.Contains);
        var overlap = (double)matched / promptTokens.Count;

        var score = overlap >= OverlapBudget
            ? 1.0
            : Floor + (1.0 - Floor) * (overlap / OverlapBudget);

        var notes = new List<string>
        {
            $"{matched}/{promptTokens.Count} salient prompt terms found in the response ({overlap:P0} overlap)"
        };

        return new StaticAnalysisFinding(Analyzer: Name, Score: score, Notes: notes);
    }

    /// <summary>Lower-cases, tokenizes on word boundaries, and drops stop-words and very short tokens.</summary>
    private static HashSet<string> ExtractSalientTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Token().Matches(text))
        {
            var word = match.Value;
            if (word.Length < 3 || StopWords.Contains(word)) continue;

            tokens.Add(word);
        }

        return tokens;
    }

    [GeneratedRegex(pattern: "[A-Za-z][A-Za-z0-9_]*", options: RegexOptions.None, 1000)]
    private static partial Regex Token();
}
