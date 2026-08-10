namespace TotallyHot.ArcRouter.Sandbox.Extraction;

/// <summary>
/// A simple keyword-heuristic dimension inferrer over the prompt text. Deliberately minimal (see
/// architecture doc §10, "start simple and refine"); defaults to <c>code_generation</c>.
/// </summary>
public sealed class KeywordDimensionInferrer : IDimensionInferrer
{
    /// <summary>
    /// Language names whose co-occurrence in a prompt (or an explicit porting phrase) signals a
    /// cross-language task rather than a single-language one. Deliberately checked before every other
    /// branch: <see cref="RouterDimension.MultiLanguage"/> is otherwise unreachable, since a prompt
    /// naming two languages just as easily matches a later branch's keywords (e.g. "port this
    /// algorithm from Python to Go" also contains "algorithm").
    /// </summary>
    private static readonly string[] LanguageNames =
    [
        "python", "javascript", "typescript", "c#", "csharp", "java", "go", "golang", "rust",
        "c++", "ruby", "php", "kotlin", "swift",
    ];

    /// <inheritdoc />
    public string Infer(string prompt, SandboxLanguage language)
    {
        var p = (prompt ?? string.Empty).ToLowerInvariant();

        if (ContainsAny(p, "port to", "port from", "translate to", "translate from", "convert from", "rewrite in", "cross-language", "cross language") ||
            CountDistinctLanguageMentions(p) >= 2)
        {
            return RouterDimension.MultiLanguage;
        }

        if (ContainsAny(p, "fix", "bug", "error", "exception", "stack trace", "traceback", "doesn't work", "not working"))
        {
            return RouterDimension.BugFixing;
        }

        if (ContainsAny(p, "refactor", "clean up", "rename", "restructure", "simplify"))
        {
            return RouterDimension.CodeRefactoring;
        }

        if (ContainsAny(p, "unit test", "write tests", "test case", "pytest", "xunit", "jest"))
        {
            return RouterDimension.TestGeneration;
        }

        if (ContainsAny(p, "explain", "what does", "how does", "understand", "walk through"))
        {
            return RouterDimension.CodeUnderstanding;
        }

        if (ContainsAny(p, "complexity", "algorithm", "optimize", "big-o", "sort", "search", "dynamic programming"))
        {
            return RouterDimension.AlgorithmDesign;
        }

        if (ContainsAny(p, "dataframe", "pandas", "numpy", "plot", "dataset", "regression"))
        {
            return RouterDimension.DataScience;
        }

        if (ContainsAny(p, "complete", "finish", "fill in", "autocomplete"))
        {
            return RouterDimension.CodeCompletion;
        }

        return RouterDimension.CodeGeneration;
    }

    /// <summary>Returns true if the haystack contains any of the given needles, using an ordinal substring match.</summary>
    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Counts how many distinct entries of <see cref="LanguageNames"/> appear in the (already
    /// lowercased) prompt - two or more is this heuristic's cross-language signal. Requires word boundaries
    /// to avoid false positives (e.g., "go" in regular prose, "java" inside "javascript").</summary>
    private static int CountDistinctLanguageMentions(string lowercasePrompt)
    {
        var count = 0;
        foreach (var name in LanguageNames)
        {
            if (MatchesAsLanguageName(lowercasePrompt, name))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Checks if a language name appears as a whole word or hyphenated phrase, not as a substring.</summary>
    private static bool MatchesAsLanguageName(string text, string languageName)
    {
        var index = 0;
        while ((index = text.IndexOf(languageName, index, StringComparison.Ordinal)) >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var after = index + languageName.Length >= text.Length || !char.IsLetterOrDigit(text[index + languageName.Length]);
            if (before && after)
            {
                return true;
            }

            index += languageName.Length;
        }

        return false;
    }
}

