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
}
