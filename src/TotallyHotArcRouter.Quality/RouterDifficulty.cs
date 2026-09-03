namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// The router's difficulty vocabulary, per the worked few-shot example in the research doc
/// (§ "Few-Shot Prompt", e.g. <c>Difficulty: medium</c>). Sibling to <see cref="RouterDimension"/>:
/// the single owner of the string constants so a classifier and a consumer of its output can never
/// drift onto different literals for the same tier.
/// </summary>
public static class RouterDifficulty
{
    /// <summary>A short, low-ambiguity task with little room for a wrong turn.</summary>
    public const string Easy = "easy";

    /// <summary>The default tier for a task with no strong signal either way.</summary>
    public const string Medium = "medium";

    /// <summary>
    /// A long or structurally complex task, or one whose prompt names a known hard signal
    /// (concurrency, distributed systems, performance-critical code, and the like).
    /// </summary>
    public const string Hard = "hard";
}