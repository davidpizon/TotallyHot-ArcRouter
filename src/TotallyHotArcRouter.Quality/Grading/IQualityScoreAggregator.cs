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
    /// Submits a freshly graded static result. Either writes it immediately (no judge expected) or holds it
    /// open for <see cref="CompleteWithJudgeAsync"/>.
    /// </summary>
    /// <param name="result">The static result to submit.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes once the result has been written or accepted for holding.</returns>
    Task SubmitAsync(QualityResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Supplies the judge's grade for a held result, blending and writing it. A correlation id that is not
    /// held - already timed out, evicted, or never submitted - is ignored.
    /// </summary>
    /// <param name="correlationId">The correlation id identifying the held result.</param>
    /// <param name="judgeScore">The judge's grade, normalized to [0,1].</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a held result was completed by this call; otherwise <see langword="false"/>.</returns>
    Task<bool> CompleteWithJudgeAsync(string correlationId, double judgeScore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a held result immediately with its static score alone, because the judge is known not to be
    /// coming - it abstained, its backbone failed, or the response text it needed had already aged out.
    /// </summary>
    /// <param name="correlationId">The correlation id identifying the held result.</param>
    /// <param name="reason">A short machine-readable reason, recorded as the result's degraded reason.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a held result was released by this call; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Waiting out the full join timeout would produce the same score, just a minute later. Releasing
    /// eagerly matters because the judge's own failure modes are common and cheap to detect - an operator
    /// with no eligible free model configured would otherwise have every score arrive a timeout late, and
    /// would reasonably read that as the verifier being broken.
    /// </remarks>
    Task<bool> AbandonJudgeAsync(string correlationId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes out every held result whose judge wait has expired, using its static score alone. Called
    /// periodically by <see cref="QualityJoinSweepService"/>, and directly by tests.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of results written by this sweep.</returns>
    Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default);
}