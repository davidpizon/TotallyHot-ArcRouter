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

    /// <summary>
    /// A counted user signal contradicted the grade of record (issue #304 phase 4). Scores stay for
    /// audit; the row does not train.
    /// </summary>
    public const string Disputed = "disputed";

    /// <summary>
    /// The reply was only tool calls, so there is no text to grade (issue #304 phase 6). The row is
    /// stored for the dashboard and does not learn.
    /// </summary>
    public const string DeferredToLoop = "deferred_to_loop";

    /// <summary>
    /// A text reply followed tool results, and the chain that would name the starting question does
    /// not exist yet (issue #304 phase 6). The row does not learn, and no question is invented.
    /// </summary>
    public const string LoopUnlinked = "loop_unlinked";
}

/// <summary>Who produced the grade of record on a receipt (issue #304). Null when no score was produced.</summary>
public static class GradeSources
{
    /// <summary>The pinned free teacher produced <see cref="QualityResult.JudgeScore"/>.</summary>
    public const string Teacher = "teacher";
}

/// <summary>
/// Counted user or implicit feedback events stored on <c>grade_receipts.user_signal</c> (issue #304
/// phase 4). Not a grade axis and not a 1–5 score.
/// </summary>
public static class UserSignals
{
    /// <summary>An explicit client pick of another model for the same question HMAC.</summary>
    public const string AgentSwitch = "agent_switch";

    /// <summary>A short next-turn thanks / "that worked" message in the same session.</summary>
    public const string Thanks = "thanks";
}
