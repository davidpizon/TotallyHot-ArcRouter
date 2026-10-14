using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// One unified grade receipt persisted to <c>grade_receipts</c> (issue #293 plan §2.4): links a
/// <see cref="QualityResult"/> to its question HMAC and grading metadata without storing question text.
/// </summary>
/// <param name="CorrelationId">Primary key; joins to <c>grader_scores</c> and transcript rows.</param>
/// <param name="CreatedAtUtc">When this receipt was written.</param>
/// <param name="Dimension">The task dimension the request was routed under.</param>
/// <param name="Model">The candidate model that produced the graded response.</param>
/// <param name="GradeStatus">Whether the grade may train the router.</param>
/// <param name="UnifiedScore">The fixed-weight score (display-only unless <see cref="GradeStatus.Complete"/>).</param>
/// <param name="QuestionHmac">Keyed hash of the whitespace-normalized newest user message, or null.</param>
/// <param name="QuestionChars">Character count of the newest user message before budget cutting.</param>
/// <param name="ContextMessages">How many earlier text messages were kept in the window.</param>
/// <param name="QuestionTruncated">Whether any window budget cut was applied.</param>
/// <param name="FingerprintRef">Reserved for #294; always null until fingerprinting lands.</param>
/// <param name="ScorerVersion">The <see cref="Quality.QualityOptions.ScorerVersion"/> active at grade time.</param>
/// <param name="PromptVersion">The judge prompt version when a judge ran; null when no LLM grader fired.</param>
/// <param name="WeightsVersion">The fixed-weight configuration stamp active at grade time.</param>
/// <param name="AbstainedGraders">
/// Comma-separated <c>grader:reason</c> pairs; empty on <see cref="GradeStatus.Complete"/>.
/// </param>
public sealed record GradeReceiptRecord(
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    string Dimension,
    string Model,
    GradeStatus GradeStatus,
    double UnifiedScore,
    string? QuestionHmac,
    int? QuestionChars,
    int? ContextMessages,
    bool QuestionTruncated,
    string? FingerprintRef,
    string ScorerVersion,
    string? PromptVersion,
    string WeightsVersion,
    string AbstainedGraders);
