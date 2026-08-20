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

    /// <summary>
    /// Loads up to <paramref name="limit"/> transcript IDs where <c>memory_entry_id IS NULL AND score IS NOT NULL</c>,
    /// ordered by <c>id ASC</c> (oldest first), for embedding backfill by Phase T1d. Returns an empty list if
    /// transcript capture is disabled or no unembedded scored rows exist.
    /// </summary>
    /// <param name="limit">The maximum number of row ids to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A read-only list of transcript row ids, up to <paramref name="limit"/> in size, or empty if none match.</returns>
    Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the full row for a transcript by its id, used by embedding backfill to obtain the
    /// <see cref="TranscriptRecord.PromptText"/> for embedding. Returns <see langword="null"/> if
    /// transcript capture is disabled or no row matches the id.
    /// </summary>
    /// <param name="id">The transcript row id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The matching transcript record, or <see langword="null"/> if not found.</returns>
    Task<TranscriptRecord?> GetTranscriptAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links a transcript row to its backfilled <c>memory_entries</c> entry by updating the
    /// <c>memory_entry_id</c> column. A no-op when transcript capture is disabled.
    /// </summary>
    /// <param name="transcriptId">The transcript row id.</param>
    /// <param name="memoryEntryId">The memory entry id to link.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task LinkMemoryEntryAsync(long transcriptId, long memoryEntryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total number of rows in <c>request_transcripts</c>, used by retention to enforce
    /// the <see cref="TranscriptOptions.MaxRows"/> bound. Returns 0 if transcript capture is disabled.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The row count, or 0 if capture is disabled.</returns>
    Task<int> GetRowCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the oldest <paramref name="count"/> rows from <c>request_transcripts</c>, used by
    /// retention to enforce the <see cref="TranscriptOptions.MaxRows"/> bound when it is exceeded.
    /// A no-op when transcript capture is disabled.
    /// </summary>
    /// <param name="count">The number of oldest rows to delete.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all rows where <c>created_at_utc &lt; <paramref name="cutoff"/></c>, used by retention
    /// to enforce the <see cref="TranscriptOptions.RetentionDays"/> bound. A no-op when transcript capture
    /// is disabled.
    /// </summary>
    /// <param name="cutoff">The exclusive UTC timestamp cutoff; rows older than this are deleted.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the prompt text of every transcript row linked to a <c>memory_entries</c> row, keyed by
    /// <c>memory_entry_id</c> - used by the cluster trainer's top-TF-IDF-term naming
    /// (docs/router/self-organizing-classification-plan.md Phase T2e), the one piece of cluster-model
    /// provenance that genuinely needs prompt text rather than just the dimension label already carried on
    /// <see cref="Router.MemoryEntry.Dimension"/>. Returns an empty map if transcript capture is disabled
    /// or no linked rows carry prompt text.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A map from <c>memory_entry_id</c> to that row's prompt text.</returns>
    Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(CancellationToken cancellationToken = default);
}
