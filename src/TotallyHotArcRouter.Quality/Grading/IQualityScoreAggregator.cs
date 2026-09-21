namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Joins the two independent grades for one request - the static verdict and the G-Eval judge's opinion -
/// and guarantees that exactly one score per request reaches the router's memory.
/// </summary>
/// <remarks>
/// <b>Why a join exists at all.</b> Router memory keeps a running sum and count per (dimension, model)
/// pair. If both graders wrote, a judged request would count twice: it would inflate the sample size the
/// voters trust and average two different scales together, so a model would look more-measured simply for
/// having been judged. Holding the static verdict until the judge answers - or until the wait expires -
/// keeps the contract at one observation per request, whichever graders contributed to it.
/// </remarks>
public interface IQualityScoreAggregator
{
    /// <summary>
    /// Submits a freshly graded static result. Either writes it immediately (no pending grader) or holds it
    /// open for <see cref="CompleteGraderAsync"/>.
    /// </summary>
    /// <param name="result">The static result to submit.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes once the result has been written or accepted for holding.</returns>
    Task SubmitAsync(QualityResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Supplies a grader's score for a held result. When <paramref name="graderKey"/> is
    /// <see cref="GraderKeys.Judge"/>, the score also lands on <see cref="QualityResult.JudgeScore"/> so the
    /// named judge axis and downstream <c>IsJudgeScored</c> provenance keep working; every other key lands
    /// in <see cref="QualityResult.GraderScores"/>. A correlation id that is not held - already timed out,
    /// evicted, or never submitted - is ignored.
    /// </summary>
    /// <param name="correlationId">The correlation id identifying the held result.</param>
    /// <param name="graderKey">The grader's key, e.g. <see cref="GraderKeys.Judge"/> or <see cref="GraderKeys.CodeJudge"/>.</param>
    /// <param name="score">The grader's score, normalized to [0,1].</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a held result was completed by this call; otherwise <see langword="false"/>.</returns>
    Task<bool> CompleteGraderAsync(string correlationId, string graderKey, double score,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases one grader's contribution for a held result because it is known not to be coming - it
    /// abstained, its backbone failed, or the response text it needed had already aged out.
    /// </summary>
    /// <param name="correlationId">The correlation id identifying the held result.</param>
    /// <param name="graderKey">The grader's key, e.g. <see cref="GraderKeys.Judge"/> or <see cref="GraderKeys.CodeJudge"/>.</param>
    /// <param name="reason">A short machine-readable reason, recorded in the result's per-grader degraded reasons.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a held result was found (and, if it was the last pending grader, written); otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Waiting out the full join timeout would produce the same score, just a minute later. Releasing
    /// eagerly matters because a grader's own failure modes are common and cheap to detect - an operator
    /// with no eligible free model configured would otherwise have every score arrive a timeout late, and
    /// would reasonably read that as the verifier being broken.
    /// </remarks>
    Task<bool> AbandonGraderAsync(string correlationId, string graderKey, string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes out every held result whose judge wait has expired, using its static score alone. Called
    /// periodically by <see cref="QualityJoinSweepService"/>, and directly by tests.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of results written by this sweep.</returns>
    Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default);
}
