namespace TotallyHot.ArcRouter.Sandbox.Extraction;

/// <summary>
/// A simple keyword-heuristic dimension inferrer over the prompt text. Deliberately minimal (see
/// architecture doc §10, "start simple and refine"); defaults to <c>code_generation</c>.
/// </summary>
public sealed class KeywordDimensionInferrer : IDimensionInferrer
{
    /// <inheritdoc />
    public string Infer(string prompt, SandboxLanguage language)
    {
        var p = (prompt ?? string.Empty).ToLowerInvariant();

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
}

