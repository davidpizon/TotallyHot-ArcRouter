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
    /// Loads up to <paramref name="limit"/> transcript ids whose saved response text has not yet been
    /// graded by the scorer identified by <paramref name="scorerVersion"/>, oldest first, for
    /// <see cref="QualityRescanService"/>. Returns an empty list when transcript capture is disabled or
    /// every row is current.
    /// </summary>
    /// <param name="scorerVersion">The current <c>Quality:ScorerVersion</c>; rows already stamped with it are excluded.</param>
    /// <param name="limit">The maximum number of row ids to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Transcript row ids needing a grade, up to <paramref name="limit"/> in size.</returns>
    /// <remarks>
    /// Rows with no <c>response_text</c> are excluded rather than returned and skipped: there is nothing to
    /// grade, and returning them would let a run of text-less rows consume an entire batch and starve the
    /// sweep. A row whose text is present but yields no code block is still stamped by
    /// <see cref="MarkQualityRescannedAsync"/> with a null score, so it leaves the pending set instead of
    /// being retried every sweep forever.
    /// </remarks>
    Task<IReadOnlyList<long>> LoadPendingQualityRescanAsync(
        string scorerVersion,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of one rescan: writes <paramref name="score"/> and stamps
    /// <paramref name="scorerVersion"/> onto the row, so the sweep does not pick it up again until the
    /// scorer changes. A no-op when transcript capture is disabled or no row matches.
    /// </summary>
    /// <param name="transcriptId">The transcript row id.</param>
    /// <param name="scorerVersion">The scorer version to stamp.</param>
    /// <param name="score">The freshly graded score, or <see langword="null"/> when the row yielded nothing gradable.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <remarks>
    /// The score is **overwritten**, not merged - a rescan under a changed scorer is meant to replace the
    /// old scorer's verdict, which is what makes the corpus re-measurable. Note that any
    /// <c>taxonomy_comparisons</c> row already computed from the previous score is not recomputed, so a
    /// rescan can leave a comparison keyed to a score that has since moved; the comparison is a historical
    /// record of what was decided at the time, and rewriting it would fabricate a decision that was never
    /// made.
    /// </remarks>
    Task MarkQualityRescannedAsync(
        long transcriptId,
        string scorerVersion,
        double? score,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Returns each model's observed mean prompt and completion token counts across every captured row
    /// that recorded both - the estimator
    /// docs/router/self-organizing-classification-plan.md Phase T4 prices its counterfactual with.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A map from the routed model name (as captured) to its observed averages; empty when capture is disabled.</returns>
    /// <remarks>
    /// The counterfactual model's true token count for a given request is never observed - it was never
    /// asked to serve it - so the phase's cost figure is explicitly an estimate, and this is what makes it
    /// one. A model with no captured rows is absent from the map rather than defaulted, so a caller states
    /// "no estimate" instead of pricing an invented token count.
    /// </remarks>
    Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One model's observed mean token usage across captured transcripts - the per-model estimator behind
/// Phase T4's counterfactual cost figure.
/// </summary>
/// <param name="InputTokens">Mean prompt tokens observed for this model.</param>
/// <param name="OutputTokens">Mean completion tokens observed for this model.</param>
/// <param name="ObservationCount">How many captured rows back these means.</param>
public sealed record ModelTokenAverage(double InputTokens, double OutputTokens, int ObservationCount);
