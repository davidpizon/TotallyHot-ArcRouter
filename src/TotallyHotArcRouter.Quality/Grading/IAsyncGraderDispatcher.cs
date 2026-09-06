namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Starts every asynchronous grader a held <see cref="QualityResult"/> is waiting on, at the moment
/// <see cref="QualityScoreAggregator.SubmitAsync"/> opens the hold - not at the eventual write. Fixing that
/// trigger point is the whole reason this seam exists: an earlier design fired the judge from
/// <see cref="IQualityScoreObserver.ObserveAsync"/>, which only runs once a held result is written, and a
/// result needing judgment is never written until the judge resolves it. That produced a deadlock broken
/// only by <see cref="QualityScoreAggregator.SweepExpiredAsync"/>'s timeout - every judged request paid the
/// full join wait for a grade that was never actually requested until it was too late to matter
/// (docs/router/judge-join-deadlock-fix-plan.md).
/// </summary>
/// <remarks>
/// This is a seam, not a policy, mirroring <see cref="IJudgeAvailability"/>: the judge lives in the host
/// application (it needs provider configuration, an HTTP stack, and the operator's model choice), and this
/// assembly deliberately does not reference it. The host supplies the real implementation;
/// <see cref="NoAsyncGraderDispatcher"/> is the safe default that keeps the verifier fully functional on its
/// own. A future asynchronous grader (Phase Q3) is started through this same seam rather than requiring its
/// own trigger mechanism.
/// </remarks>
public interface IAsyncGraderDispatcher
{
    /// <summary>
    /// Starts grading for as many of <paramref name="pendingGraderKeys"/> as this dispatcher can actually
    /// hand off right now, and reports back which ones it accepted.
    /// </summary>
    /// <param name="result">The freshly held static result, before any asynchronous grader has contributed.</param>
    /// <param name="pendingGraderKeys">The grader keys the aggregator is holding this result open for.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The subset of <paramref name="pendingGraderKeys"/> actually dispatched. Every key not returned is
    /// abandoned by the caller immediately rather than waiting out the full join timeout for a grade that
    /// was never requested - the same reasoning <see cref="IQualityScoreAggregator.AbandonJudgeAsync"/>
    /// already documents. Returning an empty set is always safe; returning a key not present in
    /// <paramref name="pendingGraderKeys"/> has no effect.
    /// </returns>
    Task<IReadOnlySet<string>> DispatchAsync(
        QualityResult result,
        IReadOnlySet<string> pendingGraderKeys,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IAsyncGraderDispatcher"/>: nothing is ever dispatched, so a held result always
/// falls back to <see cref="QualityScoreAggregator.SweepExpiredAsync"/>'s timeout unless
/// <see cref="IJudgeAvailability"/> itself already answered <see langword="false"/>. Registered whenever the
/// host has not supplied its own, which keeps this assembly usable standalone and in tests.
/// </summary>
public sealed class NoAsyncGraderDispatcher : IAsyncGraderDispatcher
{
    /// <inheritdoc/>
    public Task<IReadOnlySet<string>> DispatchAsync(
        QualityResult result,
        IReadOnlySet<string> pendingGraderKeys,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
