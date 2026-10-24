namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Persists and purges rows in <c>grade_receipts</c> (issue #293 plan §2.4).
/// </summary>
public interface IGradeReceiptStore : IScoreRetentionStore
{
    /// <summary>Inserts or replaces the receipt for <paramref name="record"/>'s correlation id.</summary>
    /// <param name="record">The receipt to persist.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task UpsertAsync(GradeReceiptRecord record, CancellationToken cancellationToken = default);

    /// <summary>Reads the receipt for <paramref name="correlationId"/>, if one exists.</summary>
    /// <param name="correlationId">The correlation id to look up.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stored receipt, or <see langword="null"/> when none exists.</returns>
    Task<GradeReceiptRecord?> TryGetAsync(string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest <see cref="Quality.GradeStatus.Complete"/> receipt in <paramref name="sessionId"/>,
    /// for the thanks detector (issue #304 phase 4).
    /// </summary>
    /// <param name="sessionId">The session to search.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The latest complete receipt, or null when none exists.</returns>
    Task<GradeReceiptRecord?> TryGetLatestCompleteInSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest <see cref="Quality.GradeStatus.Complete"/> receipt whose question HMAC matches
    /// <paramref name="questionHmac"/>, for the agent-switch detector (issue #304 phase 4).
    /// </summary>
    /// <param name="questionHmac">The keyed hash of the newest user message.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The latest matching complete receipt, or null when none exists.</returns>
    Task<GradeReceiptRecord?> TryGetLatestCompleteByQuestionHmacAsync(
        string questionHmac,
        CancellationToken cancellationToken = default);
}
