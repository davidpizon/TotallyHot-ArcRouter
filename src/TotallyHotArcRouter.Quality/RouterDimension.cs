namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// The single owner of the router's dimension vocabulary (research-doc §4.4) and the <c>live:</c>-style
/// prefix used to namespace heuristic, request-time scores from the checked-in benchmark matrices
/// (architecture doc §15). Every writer and reader of a live <c>RouterMemory</c> key (in
/// <c>TotallyHot.ArcRouter.Router</c>) must construct it through <see cref="ToLiveKey"/> rather than
/// hand-writing a <c>"live:" + dimension</c> literal, so the two sides can never independently drift
/// into a mismatch - see PLAN.md Phase G.
/// </summary>
public static class RouterDimension
{
    /// <summary>Function-level synthesis from a natural-language description.</summary>
    public const string CodeGeneration = "code_generation";

    /// <summary>Competitive-programming-style algorithmic problems.</summary>
    public const string AlgorithmDesign = "algorithm_design";

    /// <summary>Locating and repairing a defect in existing code.</summary>
    public const string BugFixing = "bug_fixing";

    /// <summary>Fill-in-the-middle completion of a partial snippet.</summary>
    public const string CodeCompletion = "code_completion";

    /// <summary>Improving existing code's quality/structure without changing its behavior.</summary>
    public const string CodeRefactoring = "code_refactoring";

    /// <summary>Data-analysis pipelines (e.g. pandas/numpy).</summary>
    public const string DataScience = "data_science";

    /// <summary>Cross-language tasks (e.g. porting a snippet between languages).</summary>
    public const string MultiLanguage = "multi_language";

    /// <summary>Explaining or summarizing existing code.</summary>
    public const string CodeUnderstanding = "code_understanding";

    /// <summary>Generating a test suite for existing code.</summary>
    public const string TestGeneration = "test_generation";

    /// <summary>
    /// All nine single-turn coding dimensions (research-doc §4.4), in the table's order. Does not include
    /// the out-of-distribution "Agentic Programming" dimension, which this router does not classify
    /// requests into.
    /// </summary>
    public static readonly IReadOnlyList<string> AllDimensions =
    [
        CodeGeneration,
        AlgorithmDesign,
        BugFixing,
        CodeCompletion,
        CodeRefactoring,
        DataScience,
        MultiLanguage,
        CodeUnderstanding,
        TestGeneration,
    ];

    /// <summary>
    /// Builds the <c>RouterMemory</c> key for a live, request-time observation of
    /// <paramref name="dimension"/>: <paramref name="liveMemoryPrefix"/> (typically
    /// <see cref="QualityOptions.LiveMemoryPrefix"/>) concatenated with the dimension. The sole
    /// construction point for this key - every writer and reader must call it rather than
    /// concatenating the prefix themselves, so the write side and the read side can never drift apart.
    /// </summary>
    /// <param name="liveMemoryPrefix">The configured live-namespace prefix (e.g. <c>"live:"</c>).</param>
    /// <param name="dimension">The dimension, typically one of <see cref="AllDimensions"/>.</param>
    /// <returns>The composed <c>RouterMemory</c> key.</returns>
    public static string ToLiveKey(string liveMemoryPrefix, string dimension) => liveMemoryPrefix + dimension;
}
