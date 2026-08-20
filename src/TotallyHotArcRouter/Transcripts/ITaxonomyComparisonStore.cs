namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Persists and queries <see cref="TaxonomyComparisonRecord"/> rows
/// (docs/router/self-organizing-classification-plan.md Phase T4). Kept separate from
/// <see cref="ITranscriptStore"/> despite sharing a database file: transcripts are raw capture, these are
/// derived measurements, and a reader that wants one rarely wants the other.
/// </summary>
/// <remarks>
/// Like <see cref="ITranscriptStore"/>, every method no-ops when transcript capture is disabled - with no
/// transcripts there is nothing to compare, so the whole feature is correctly inert rather than
/// separately switchable.
/// </remarks>
public interface ITaxonomyComparisonStore
{
    /// <summary>
    /// Returns up to <paramref name="limit"/> transcript row ids that are ready to compare and have not
    /// been compared yet - scored, linked to a <c>memory_entries</c> row (so an embedding exists to assign
    /// a cluster from), and carrying no <c>taxonomy_comparisons</c> row. Oldest first.
    /// </summary>
    /// <param name="limit">The maximum number of ids to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The pending transcript ids, or an empty list when capture is disabled or nothing is pending.</returns>
    /// <remarks>
    /// This is the queue Phase T4's background job drains. Readiness deliberately requires the embedding
    /// link rather than just a score: the learned taxonomy cannot assign a cluster without one, and
    /// comparing a row that only one of the two taxonomies could speak to would bias the comparison toward
    /// whichever side happened to have data.
    /// </remarks>
    Task<IReadOnlyList<long>> LoadPendingComparisonsAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Persists one comparison row, replacing any existing row for the same transcript.</summary>
    /// <param name="record">The comparison to persist.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task UpsertAsync(TaxonomyComparisonRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads every comparison row whose <see cref="TaxonomyComparisonRecord.ComparedAtUtc"/> falls at or
    /// after <paramref name="since"/>, optionally restricted to one session.
    /// </summary>
    /// <param name="since">The inclusive lower bound on comparison time.</param>
    /// <param name="sessionId">A session to filter to, or <see langword="null"/> for every session.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The matching rows, oldest first.</returns>
    Task<IReadOnlyList<TaxonomyComparisonRecord>> LoadSinceAsync(
        DateTimeOffset since,
        string? sessionId = null,
        CancellationToken cancellationToken = default);
}
