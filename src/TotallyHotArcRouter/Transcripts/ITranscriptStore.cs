namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// Persists <see cref="TranscriptRecord"/> rows for the opt-in transcript store
/// (docs/router/self-organizing-classification-plan.md Phase T1). A no-op implementation is expected when
/// <see cref="TranscriptOptions.Enabled"/> is <see langword="false"/>, so callers never need their own
/// enabled check - every method degrades to "nothing happened" rather than throwing.
/// </summary>
public interface ITranscriptStore
{
    /// <summary>
    /// Persists a new transcript row. A no-op (returning <see langword="null"/>) when transcript capture
    /// is disabled.
    /// </summary>
    /// <param name="record">The row to persist. Its <see cref="TranscriptRecord.Id"/> is ignored.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The store-assigned id of the inserted row, or <see langword="null"/> when capture is disabled.</returns>
    Task<long?> InsertAsync(TranscriptRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Backfills the <c>score</c> column for the row matching <paramref name="correlationId"/>, once the
    /// verifier's score arrives. A no-op when transcript capture is disabled or no row matches.
    /// </summary>
    /// <param name="correlationId">The correlation id shared with the row's <see cref="TranscriptRecord.CorrelationId"/>.</param>
    /// <param name="score">The verifier's observed quality score in [0, 1].</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task UpdateOutcomeAsync(string correlationId, double? score, CancellationToken cancellationToken = default);
}
