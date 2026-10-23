namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Stable grading-recipe identifiers (issue #304 / ADR-0027). A recipe is a weight vector over judge,
/// syntax, and analysis, not a <c>RouterDimension</c> topic name. Until a bucket version exists, unfenced
/// text uses <see cref="Bootstrap"/> and a fenced reply keeps <see cref="Code"/>.
/// </summary>
public static class GradingRecipes
{
    /// <summary>The #293 code recipe: judge 0.60, syntax 0.25, analysis 0.15, keyed by dimension.</summary>
    public const string Code = "code";

    /// <summary>The temporary non-code recipe: judge 1.0, syntax 0, analysis 0.</summary>
    public const string Bootstrap = "bootstrap";

    /// <summary>
    /// Whether <paramref name="recipeId"/> is the code recipe, including the empty default used by
    /// call sites that predate this field.
    /// </summary>
    /// <param name="recipeId">The recipe id on a request or result; may be null or empty.</param>
    /// <returns><see langword="true"/> when live <c>RouterDimension</c> tallies may be updated.</returns>
    public static bool IsCodeRecipe(string? recipeId)
    {
        return string.IsNullOrEmpty(recipeId) ||
               string.Equals(recipeId, Code, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// <c>incomplete_reason</c> values this slice writes onto a grade receipt (issue #304). Empty on
/// <see cref="GradeStatus.Complete"/>. Grader abstentions #293 already records in
/// <c>abstained_graders</c> are not copied here.
/// </summary>
public static class IncompleteReasons
{
    /// <summary>The grading queue rejected the enqueue.</summary>
    public const string QueueFull = "queue_full";

    /// <summary>No extractable response text, and no tool call was seen.</summary>
    public const string NoText = "no_text";

    /// <summary>The response text was present but blank (whitespace only).</summary>
    public const string Empty = "empty";

    /// <summary>The judge is on, but the pinned free model is missing, paid, or switched off.</summary>
    public const string NoJudge = "no_judge";

    /// <summary>The answering model is the pinned judge; that response is never graded.</summary>
    public const string JudgeIsAnswerer = "judge_is_answerer";
}

/// <summary>Who produced the grade of record on a receipt (issue #304). Null when no score was produced.</summary>
public static class GradeSources
{
    /// <summary>The pinned free teacher produced <see cref="QualityResult.JudgeScore"/>.</summary>
    public const string Teacher = "teacher";
}
