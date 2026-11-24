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
    /// Backfills the <c>score</c> column (and <see cref="TranscriptRecord.IsJudgeScored"/>) for the row
    /// matching <paramref name="correlationId"/>, once the verifier's score arrives. A no-op when
    /// transcript capture is disabled or no row matches.
    /// </summary>
    /// <param name="correlationId">The correlation id shared with the row's <see cref="TranscriptRecord.CorrelationId"/>.</param>
    /// <param name="score">The verifier's observed quality score in [0, 1].</param>
    /// <param name="isJudgeScored">
    /// Whether the judge actually contributed a grade that fed <paramref name="score"/>, for
    /// <see cref="TranscriptRecord.IsJudgeScored"/>. Defaults to <see langword="false"/> for a caller that
    /// does not track judge provenance.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task UpdateOutcomeAsync(string correlationId, double? score, bool isJudgeScored = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Backfills the score and, when <paramref name="scorerVersion"/> is set, stamps that version onto
    /// the row so the quality-rescan sweep does not overwrite a live non-code grade with null
    /// (issue #304 / ADR-0027). Default forwards to the three-argument overload so existing fakes compile.
    /// </summary>
    /// <param name="correlationId">The correlation id shared with the row's <see cref="TranscriptRecord.CorrelationId"/>.</param>
    /// <param name="score">The verifier's observed quality score in [0, 1].</param>
    /// <param name="isJudgeScored">Whether the judge contributed a grade that fed <paramref name="score"/>.</param>
    /// <param name="scorerVersion">
    /// The current <c>Quality:ScorerVersion</c> to stamp, or <see langword="null"/> to leave the column
    /// unchanged (the code-recipe live path).
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task UpdateOutcomeAsync(
        string correlationId,
        double? score,
        bool isJudgeScored,
        string? scorerVersion,
        CancellationToken cancellationToken = default)
        => UpdateOutcomeAsync(correlationId, score, isJudgeScored, cancellationToken);

    /// <summary>
    /// Loads up to <paramref name="limit"/> transcript IDs where <c>memory_entry_id IS NULL AND score IS NOT NULL</c>,
    /// ordered by <c>id ASC</c> (oldest first), for embedding backfill by Phase T1d. Only rows with a session-file
    /// turn and a stored prompt length are returned: a row with no text to embed would otherwise head every batch
    /// forever (#165 phase 2). Returns an empty list if
    /// transcript capture is disabled or no unembedded scored rows exist.
    /// </summary>
    /// <param name="limit">The maximum number of row ids to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A read-only list of transcript row ids, up to <paramref name="limit"/> in size, or empty if none match.</returns>
    Task<IReadOnlyList<long>> LoadUnembeddedScoredAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the full row for a transcript by its id. Prompt and response text, when present, come from
    /// the session file's Extracts frame rather than from <c>request_transcripts</c>. Returns <see langword="null"/> if
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
    /// Rows with no stored response (a null or zero <c>response_text_length</c>) are excluded rather than
    /// returned and skipped: there is nothing to
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
    /// Deletes the oldest <paramref name="count"/> rows from <c>request_transcripts</c>.
    /// Retired for production retention (#165 phase 2): Sample Size session retention is the deleter.
    /// Kept for secure-deletion tests that plant canaries and assert the pages are gone.
    /// A no-op when transcript capture is disabled.
    /// </summary>
    /// <param name="count">The number of oldest rows to delete.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all rows where <c>created_at_utc &lt; <paramref name="cutoff"/></c>.
    /// Retired for production retention (#165 phase 2): the startup age purge was removed so session files
    /// are not left without their metadata rows. Kept for secure-deletion tests.
    /// A no-op when transcript capture is disabled.
    /// </summary>
    /// <param name="cutoff">The exclusive UTC timestamp cutoff; rows older than this are deleted.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every row from <c>request_transcripts</c> - the System Settings window's Transcription
    /// Capture "Clear" action. Unlike <see cref="DeleteOldestAsync"/> and <see cref="DeleteBeforeAsync"/>,
    /// this runs regardless of <see cref="TranscriptOptions.Enabled"/>: an operator who has just switched
    /// capture off still needs to be able to wipe what was already collected, and a database that was never
    /// created has nothing to delete either way.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes earlier deletes final by truncating the database's write-ahead log, retrying a few times over
    /// a few seconds while another connection holds it. Clear needs this because it cannot rely on the
    /// retention cycle, which does not run when capture is disabled.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when no deleted text can remain in the write-ahead log;
    /// <see langword="false"/> when the log stayed busy through every retry, so the deletion will only
    /// become final at a later checkpoint or at the next start. The default suits stores that keep no
    /// such log.
    /// </returns>
    Task<bool> FinalizeDeletionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

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
    Task<IReadOnlyDictionary<long, string>> LoadPromptTextByMemoryEntryIdAsync(
        CancellationToken cancellationToken = default);

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
    Task<IReadOnlyDictionary<string, ModelTokenAverage>> LoadObservedTokenAveragesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads up to <paramref name="limit"/> of the most recent transcript rows, newest first, for the
    /// GUI Sessions tab (docs/router/sessions-tab-training-data-plan.md Phase 1). Each row carries the
    /// <c>session_id</c> parsed from its correlation id at write time
    /// (<see cref="CorrelationIdParser.SessionIdOf"/>), so a caller can group turns into sessions the same
    /// way <c>ConversationAggregator</c> groups live telemetry, and its <c>memory_entry_id</c>, so the
    /// caller can flag which sessions were actually folded into the live-learning corpus. Returns an empty
    /// list when transcript capture is disabled.
    /// </summary>
    /// <param name="limit">The maximum number of rows to return. Must be positive.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Rows ordered by <c>id DESC</c> (most recent first), up to <paramref name="limit"/> in size.</returns>
    /// <remarks>
    /// Default-implemented to return an empty list, unlike every other member of this interface, so the
    /// eight <see cref="ITranscriptStore"/> test fakes that predate this method (none of which exercise
    /// session listing) don't all need a matching stub added. Only <see cref="SqliteTranscriptStore"/>
    /// overrides it with a real implementation; a future test that specifically exercises session listing
    /// should override it too rather than relying on this default.
    /// </remarks>
    Task<IReadOnlyList<SessionTranscript>> ListSessionsAsync(int limit, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SessionTranscript>>([]);
    }

    /// <summary>
    /// One newest-first page of one session's metadata. No prompt or response text. The default returns an
    /// empty page so existing fakes keep compiling (#176 phase 2).
    /// </summary>
    /// <param name="sessionId">The client session id.</param>
    /// <param name="limit">How many rows to return. The caller already clamped it.</param>
    /// <param name="beforeTranscriptId">When set, only rows with a smaller id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The page, newest first, one past <paramref name="limit"/> when an older row exists.</returns>
    Task<IReadOnlyList<SessionTurnMetadata>> ListSessionTurnMetadataAsync(
        string sessionId, int limit, long? beforeTranscriptId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SessionTurnMetadata>>([]);
    }

    /// <summary>
    /// Figures over every retained row of one session. The default returns null so existing fakes compile.
    /// </summary>
    /// <param name="sessionId">The client session id.</param>
    /// <param name="liveFloorTurnNumber">The lowest turn number the GUI holds live. Zero skips the number list.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The aggregate, or null when capture is off.</returns>
    Task<SessionAggregate?> GetSessionAggregateAsync(
        string sessionId, int liveFloorTurnNumber, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<SessionAggregate?>(null);
    }

    /// <summary>
    /// Records that a row's prompt cannot be read from its session file (the turn was dropped before it was
    /// written, or the file is unreadable), by zeroing <c>prompt_text_length</c>. That takes the row out of
    /// <see cref="LoadUnembeddedScoredAsync"/>, so unreadable rows cannot fill every backfill batch. The
    /// default does nothing so existing test fakes compile.
    /// </summary>
    /// <param name="id">The transcript row id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task MarkPromptUnavailableAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Same as <see cref="MarkPromptUnavailableAsync"/> keyed by <c>archive_turn_id</c>, for capture drop sites
    /// that hold the turn id but not the SQLite row id (#165 phase 2 follow-up). Idempotent when no row matches.
    /// </summary>
    /// <param name="archiveTurnId">The turn id minted when capture began.</param>
    void MarkPromptUnavailableByArchiveTurn(Guid archiveTurnId)
    {
    }

    /// <summary>
    /// Deletes transcript rows whose <c>archive_session_id</c> is one of <paramref name="archiveSessionIds"/>
    /// and returns the <c>memory_entry_id</c> values those rows pointed at, so the caller can delete the
    /// embeddings with the session (#165 phase 2).
    /// </summary>
    /// <param name="archiveSessionIds">The sessions that were just removed.</param>
    /// <returns>
    /// Memory entry ids and correlation ids that belonged to the deleted rows. Empty when this store keeps
    /// no such link. Correlation ids let the caller delete <c>response_vectors</c> rows with the session.
    /// </returns>
    TranscriptLinkDeletion DeleteByArchiveSessions(IReadOnlyCollection<Guid> archiveSessionIds) =>
        TranscriptLinkDeletion.Empty;

    /// <summary>
    /// Deletes transcript rows whose <c>archive_turn_id</c> is one of <paramref name="archiveTurnIds"/> and returns
    /// the <c>memory_entry_id</c> values those rows pointed at. Used when a turn leaves the history without its
    /// session (an import that is deleted after it filled turns into an older session), so its learned embedding
    /// does not outlive it (#165 phase 4).
    /// </summary>
    /// <param name="archiveTurnIds">The turns that were just retired.</param>
    /// <returns>
    /// Memory entry ids and correlation ids that belonged to the deleted rows. Empty when this store keeps
    /// no such link.
    /// </returns>
    TranscriptLinkDeletion DeleteByArchiveTurns(IReadOnlyCollection<Guid> archiveTurnIds) =>
        TranscriptLinkDeletion.Empty;

    /// <summary>
    /// Deletes every <c>request_transcripts</c> row with a null <c>archive_session_id</c> (legacy rows and
    /// metadata-only inserts that never bound a session file) and returns the <c>memory_entry_id</c> values
    /// those rows pointed at, together with how many rows were removed (#165 phase 2 follow-up).
    /// </summary>
    /// <returns>The purge outcome. Empty when this store keeps no such rows.</returns>
    NullArchivePurgeResult DeleteNullArchiveRows() => NullArchivePurgeResult.Empty;

    /// <summary>
    /// Loads conversation text for the given transcript ids, in request order, from session-file extracts.
    /// A missing id is returned with <see cref="StoredTurnText.Found"/> false. The default returns nothing
    /// so existing test fakes compile.
    /// </summary>
    /// <param name="transcriptIds">The <c>request_transcripts.id</c> values to load. At most a few dozen.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One result per requested id.</returns>
    Task<IReadOnlyList<StoredTurnText>> LoadTurnTextsAsync(
        IReadOnlyList<long> transcriptIds, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<StoredTurnText>>([]);
    }

    /// <summary>
    /// Returns the highest turn number recorded for <paramref name="sessionId"/> in
    /// <c>request_transcripts</c>, or 0 when none exist. Used by
    /// <see cref="Telemetry.PersistentConversationTurnTracker"/> so a restart never reissues a turn number
    /// that already has a transcript row (#176 §5.7).
    /// </summary>
    /// <remarks>
    /// Deliberately not gated on <see cref="TranscriptOptions.Enabled"/>: capture can be off at restart
    /// while earlier transcripts still sit in the database, and a gated read would return 0 and let the
    /// tracker reissue the last transcribed turn's number. Reads an already-existing database only; never
    /// creates a file, directory, or schema. A missing database file, or a file with no
    /// <c>request_transcripts</c> table, returns 0 without modifying the file.
    /// </remarks>
    /// <param name="sessionId">The session id whose highest turn number is needed.</param>
    /// <returns>The highest turn suffix found for the session, or 0.</returns>
    int GetMaxTurnNumber(string sessionId) => 0;
}

/// <summary>
/// One transcript row as read for the GUI Sessions tab
/// (docs/router/sessions-tab-training-data-plan.md Phase 1) - a subset of <see cref="TranscriptRecord"/>'s
/// columns, keyed additionally by the session id parsed out of <see cref="CorrelationId"/> so turns can be
/// grouped into sessions without a second parse in every caller.
/// </summary>
/// <param name="Id">The store-assigned row id.</param>
/// <param name="SessionId">
/// The session portion of <paramref name="CorrelationId"/> - see
/// <see cref="CorrelationIdParser.SessionIdOf"/>.
/// </param>
/// <param name="CorrelationId">The full per-request correlation id, <c>"{SessionId}:{turnNumber}"</c>.</param>
/// <param name="CreatedAtUtc">When this row was written, in UTC.</param>
/// <param name="RequestedModel">The client's literal requested model name.</param>
/// <param name="RoutedModel">The model that actually served the request.</param>
/// <param name="PromptText">The captured prompt text, or <see langword="null"/> when unavailable.</param>
/// <param name="ResponseText">The captured response text, or <see langword="null"/> when unavailable.</param>
/// <param name="Cost">The estimated dollar cost, or <see langword="null"/> when unknown.</param>
/// <param name="InputTokens">The prompt token count, or <see langword="null"/> when unknown.</param>
/// <param name="OutputTokens">The completion token count, or <see langword="null"/> when unknown.</param>
/// <param name="MemoryEntryId">
/// The linked <c>memory_entries</c> row id, or <see langword="null"/> if this transcript was never folded
/// into the live-learning corpus - the literal "used for live training" signal the Sessions tab surfaces.
/// </param>
/// <param name="PromptTextLength">
/// <paramref name="PromptText"/>'s length in characters as SQLite's <c>length()</c> counts them, or
/// <see langword="null"/> when the text is absent or the store doesn't report it. Sent to the GUI alongside a
/// display preview (#179, ADR-0023). Deliberately not <see cref="string.Length"/>, whose UTF-16 count differs
/// for emoji and other surrogate pairs.
/// </param>
/// <param name="ResponseTextLength">
/// <paramref name="ResponseText"/>'s length, counted the same way as <paramref name="PromptTextLength"/>.
/// </param>
public sealed record SessionTranscript(
    long Id,
    string SessionId,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    string RequestedModel,
    string RoutedModel,
    string? PromptText,
    string? ResponseText,
    decimal? Cost,
    int? InputTokens,
    int? OutputTokens,
    long? MemoryEntryId,
    int? PromptTextLength = null,
    int? ResponseTextLength = null);

/// <summary>
/// One model's observed mean token usage across captured transcripts - the per-model estimator behind
/// Phase T4's counterfactual cost figure.
/// </summary>
/// <param name="InputTokens">Mean prompt tokens observed for this model.</param>
/// <param name="OutputTokens">Mean completion tokens observed for this model.</param>
/// <param name="ObservationCount">How many captured rows back these means.</param>
public sealed record ModelTokenAverage(double InputTokens, double OutputTokens, int ObservationCount);

/// <summary>
/// What <see cref="ITranscriptStore.DeleteNullArchiveRows"/> removed: legacy or unbound transcript rows and
/// the memory entries those rows pointed at (#165 phase 2 follow-up).
/// </summary>
/// <param name="DeletedRows">How many <c>request_transcripts</c> rows were deleted.</param>
/// <param name="MemoryEntryIds">The <c>memory_entry_id</c> values those rows linked, for the caller to delete.</param>
/// <param name="CorrelationIds">
/// The <c>correlation_id</c> values those rows carried, so a caller can delete matching
/// <c>response_vectors</c> rows. Empty when none were deleted.
/// </param>
public readonly record struct NullArchivePurgeResult(
    int DeletedRows,
    IReadOnlyList<long> MemoryEntryIds,
    IReadOnlyList<string> CorrelationIds)
{
    /// <summary>An empty purge: nothing deleted.</summary>
    public static NullArchivePurgeResult Empty { get; } = new(0, [], []);
}

/// <summary>
/// Links a deleted transcript row still owns: the memory-entry id, when one was stored, and the
/// correlation id the response-vector table is keyed by.
/// </summary>
/// <param name="MemoryEntryIds">Memory-entry ids to delete. Empty when the rows had none.</param>
/// <param name="CorrelationIds">Correlation ids of the deleted rows.</param>
public readonly record struct TranscriptLinkDeletion(
    IReadOnlyList<long> MemoryEntryIds,
    IReadOnlyList<string> CorrelationIds)
{
    /// <summary>Nothing was deleted.</summary>
    public static TranscriptLinkDeletion Empty { get; } = new([], []);
}

/// <summary>One persisted turn's metadata for the Sessions details pane. No conversation text.</summary>
/// <param name="TranscriptId">The <c>request_transcripts</c> id.</param>
/// <param name="TurnNumber">Parsed from the correlation id, or 1 when the suffix is not a number.</param>
/// <param name="CreatedAtUtc">When the row was written.</param>
/// <param name="RequestedModel">The model the client named, already capped.</param>
/// <param name="RoutedModel">The model that served, already capped.</param>
/// <param name="Dimension">The classification dimension, or null.</param>
/// <param name="Difficulty">The classification difficulty, or null.</param>
/// <param name="Language">The classification language, or null.</param>
/// <param name="IsUtility">Whether the turn was classified as utility.</param>
/// <param name="Score">The stored score, or null.</param>
/// <param name="CostUsd">The stored cost, or null.</param>
/// <param name="IsExploratory">Whether routing explored.</param>
/// <param name="Propensity">The probability routing assigned.</param>
/// <param name="InputTokens">Prompt tokens, or null.</param>
/// <param name="OutputTokens">Completion tokens, or null.</param>
/// <param name="MemoryEntryId">The memory row, when this turn trained.</param>
/// <param name="DimBestModel">The dimension-best model, or null.</param>
/// <param name="ScorerVersion">The scorer version, or null.</param>
/// <param name="IsJudgeScored">Whether a judge grade was stored.</param>
/// <param name="UntrainedBaselineModel">The untrained baseline model, or null.</param>
/// <param name="UntrainedBaselinePredictedScore">That baseline's predicted score, or null.</param>
/// <param name="PromptTextLength">The stored prompt length, or null.</param>
/// <param name="ResponseTextLength">The stored response length, or null.</param>
/// <param name="HasToolCalls">Whether the reply carried tool calls. Null means unknown.</param>
/// <param name="Comparison">The taxonomy comparison for this row, or null.</param>
public sealed record SessionTurnMetadata(
    long TranscriptId,
    int TurnNumber,
    DateTimeOffset CreatedAtUtc,
    string RequestedModel,
    string RoutedModel,
    string? Dimension,
    string? Difficulty,
    string? Language,
    bool IsUtility,
    double? Score,
    decimal? CostUsd,
    bool IsExploratory,
    double Propensity,
    int? InputTokens,
    int? OutputTokens,
    long? MemoryEntryId,
    string? DimBestModel,
    string? ScorerVersion,
    bool IsJudgeScored,
    string? UntrainedBaselineModel,
    double? UntrainedBaselinePredictedScore,
    int? PromptTextLength,
    int? ResponseTextLength,
    bool? HasToolCalls,
    SessionBaselineComparison? Comparison);

/// <summary>The baseline comparison stored beside one transcript. Decimals stay decimal, not strings.</summary>
/// <param name="ComparedAtUtc">When the comparison was written.</param>
/// <param name="ObservedScore">The score that was compared.</param>
/// <param name="DimensionPredictedScore">The dimension head's prediction, or null.</param>
/// <param name="ClusterPredictedScore">The cluster head's prediction, or null.</param>
/// <param name="DimensionAbsError">Absolute error of the dimension head, or null.</param>
/// <param name="ClusterAbsError">Absolute error of the cluster head, or null.</param>
/// <param name="IsClustered">Whether a cluster was used.</param>
/// <param name="BaselineModel">The baseline model, already capped, or null.</param>
/// <param name="ActualCostUsd">The turn's cost, or null.</param>
/// <param name="BaselineEstimatedCostUsd">The baseline cost, or null.</param>
/// <param name="EstimatedNetSavingsUsd">Actual minus baseline, or null.</param>
/// <param name="BaselinePredictedScore">The baseline's predicted score, or null.</param>
/// <param name="EstimatedRegret">The estimated regret, or null.</param>
/// <param name="BaselineTokenizerRatio">The tokenizer ratio, or null.</param>
/// <param name="BaselineTokenizerRatioMeasured">Whether that ratio was measured, or null.</param>
/// <param name="BaselineInputTokens">Frozen input-token ingredient, or null.</param>
/// <param name="BaselineOutputTokens">Frozen output-token ingredient, or null.</param>
/// <param name="BaselineInputPricePerMillion">Frozen input price, or null.</param>
/// <param name="BaselineOutputPricePerMillion">Frozen output price, or null.</param>
public sealed record SessionBaselineComparison(
    DateTimeOffset ComparedAtUtc,
    double ObservedScore,
    double? DimensionPredictedScore,
    double? ClusterPredictedScore,
    double? DimensionAbsError,
    double? ClusterAbsError,
    bool IsClustered,
    string? BaselineModel,
    decimal? ActualCostUsd,
    decimal? BaselineEstimatedCostUsd,
    decimal? EstimatedNetSavingsUsd,
    double? BaselinePredictedScore,
    double? EstimatedRegret,
    double? BaselineTokenizerRatio,
    bool? BaselineTokenizerRatioMeasured,
    double? BaselineInputTokens,
    double? BaselineOutputTokens,
    decimal? BaselineInputPricePerMillion,
    decimal? BaselineOutputPricePerMillion);

/// <summary>Retained-history figures for one session.</summary>
/// <param name="PersistedTurns">How many rows the session still has.</param>
/// <param name="TrainedTurns">Rows with a memory entry.</param>
/// <param name="ComparedTurns">Rows with both a savings figure and a positive baseline cost.</param>
/// <param name="TotalEstimatedNetSavingsUsd">Sum of savings over compared turns, or null when none.</param>
/// <param name="TotalBaselineEstimatedCostUsd">Sum of baseline cost over those turns, or null.</param>
/// <param name="FirstTurnUtc">The oldest retained turn, or null when the session has none.</param>
/// <param name="LastTurnUtc">The newest retained turn, or null when the session has none.</param>
/// <param name="PersistedTurnNumbersFromFloor">Persisted turn numbers at or above the live floor, ascending.</param>
/// <param name="PersistedTurnNumbersComplete">False when more than 2,000 numbers sat at or above the floor.</param>
/// <param name="TotalCostUsd">Sum of known costs, or null when every row is unpriced.</param>
/// <param name="UnpricedTurns">Rows with no cost.</param>
/// <param name="TotalInputTokens">Sum of reported input tokens.</param>
/// <param name="TotalOutputTokens">Sum of reported output tokens.</param>
/// <param name="TokenUnknownTurns">Rows missing either token count.</param>
/// <param name="Models">The 20 most recently used routed models.</param>
/// <param name="OtherModels">Distinct routed models beyond those 20.</param>
/// <param name="OtherModelTurns">Turns served by those omitted models.</param>
public sealed record SessionAggregate(
    int PersistedTurns,
    int TrainedTurns,
    int ComparedTurns,
    decimal? TotalEstimatedNetSavingsUsd,
    decimal? TotalBaselineEstimatedCostUsd,
    DateTimeOffset? FirstTurnUtc,
    DateTimeOffset? LastTurnUtc,
    IReadOnlyList<int> PersistedTurnNumbersFromFloor,
    bool PersistedTurnNumbersComplete,
    decimal? TotalCostUsd,
    int UnpricedTurns,
    long TotalInputTokens,
    long TotalOutputTokens,
    int TokenUnknownTurns,
    IReadOnlyList<SessionModelUsage> Models,
    int OtherModels,
    int OtherModelTurns);

/// <summary>One routed model's share of a session's retained turns.</summary>
/// <param name="RoutedModel">The model name, already capped.</param>
/// <param name="Turns">How many retained turns it served.</param>
/// <param name="CostUsd">Sum of its priced turns, or null when none were priced.</param>
/// <param name="UnpricedTurns">Its turns with no cost.</param>
/// <param name="LastUsedUtc">When it last served a retained turn.</param>
public sealed record SessionModelUsage(
    string RoutedModel,
    int Turns,
    decimal? CostUsd,
    int UnpricedTurns,
    DateTimeOffset LastUsedUtc);