using System.Diagnostics.CodeAnalysis;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The user/task question every LLM grader conditions on. A complete, warning-free response to a
/// <em>different</em> question than the one asked must not score the same as a correct answer to this one
/// (docs/research/code-quality-metrics-assessment.md §1), so G-Eval and the CodeJudge/ICE-Score/RACE
/// portfolio all weave the recovered question into the backbone prompt and refuse to score when that
/// text is missing rather than grading the response in isolation.
/// </summary>
/// <remarks>
/// Static methods on a dedicated type rather than a file-scoped helper pile: the same presence check and
/// task-section wording have to stay identical across <see cref="GEvalJudgeClient"/> and every
/// <see cref="PortfolioGraderClientBase"/> subclass, or one grader could silently revert to
/// response-only scoring while the others stay prompt-aware.
/// </remarks>
public static class GraderQuestionText
{
    /// <summary>
    /// The abandon-reason token drain workers record when the question could not be recovered, combined
    /// with a grader prefix so the aggregator's diagnostic names both who gave up and why
    /// (<c>judge-question-missing</c>, <c>codejudge-question-missing</c>, …).
    /// </summary>
    public const string MissingReasonToken = "question-missing";

    /// <summary>
    /// Whether <paramref name="question"/> is usable as a grading requirement: non-null and not
    /// whitespace-only. Empty and whitespace are the same miss — a prompt cache hit that aged into
    /// blanks is not a task the judge can condition on.
    /// </summary>
    /// <param name="question">The recovered user/task text, or <see langword="null"/> when never cached.</param>
    /// <returns><see langword="true"/> when an LLM grader may include this text in its prompt.</returns>
    public static bool IsPresent([NotNullWhen(true)] string? question)
    {
        return !string.IsNullOrWhiteSpace(question);
    }

    /// <summary>
    /// Returns the trimmed question when it is present; otherwise throws so a caller cannot accidentally
    /// send a response-only prompt to a backbone. Drain workers check <see cref="IsPresent"/> first and
    /// abandon with a dedicated reason rather than relying on this throw; this is the last line of
    /// defense for any other <c>ScoreAsync</c> caller.
    /// </summary>
    /// <param name="question">The recovered user/task text.</param>
    /// <returns>The trimmed question, never empty.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="question"/> is missing or whitespace-only.
    /// </exception>
    public static string Require(string? question)
    {
        if (!IsPresent(question))
            throw new InvalidOperationException(
                "LLM graders cannot score a response without the user/task question it was written to answer.");

        return question.Trim();
    }

    /// <summary>
    /// The "Task the response was written for" block woven into every G-Eval and portfolio-grader prompt.
    /// Always present: this method never omits the section and never substitutes a placeholder, because
    /// either omission is the response-only grading this type exists to prevent. Callers that have not
    /// already checked <see cref="IsPresent"/> still fail closed here via <see cref="Require"/>.
    /// </summary>
    /// <param name="question">The user/task text the response was written to answer.</param>
    /// <returns>The task section, including the surrounding blank lines the prompt interpolates around.</returns>
    public static string FormatTaskSection(string question)
    {
        var required = Require(question);
        return $"""

               Task the response was written for:
               ---
               {required}
               ---

               """;
    }

    /// <summary>
    /// Builds the abandon reason a drain worker records when the question is missing, e.g.
    /// <c>judge-question-missing</c> or <c>codejudge-question-missing</c>. Kept here so the token cannot
    /// drift from the tests that assert it as a diagnostic.
    /// </summary>
    /// <param name="graderPrefix">The grader identity used as the reason prefix (<c>judge</c>, <c>codejudge</c>, …).</param>
    /// <returns>The full abandon-reason string.</returns>
    public static string MissingReason(string graderPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graderPrefix);
        return FormattableString.Invariant($"{graderPrefix}-{MissingReasonToken}");
    }
}
