namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Persists <see cref="GraderScoreRecord"/> rows for every grader (docs/router/grader-reliability-plan.md,
/// Phase Q4), supports the retention purge (<see cref="GraderScoreRetentionService"/>), and supplies the
/// bulk read <see cref="IGraderReliabilityAnalyzer"/> computes its statistics from. Mirrors
/// <see cref="IJudgeShadowScoreStore"/>'s shape.
/// </summary>
public interface IGraderScoreStore
{
    /// <summary>Persists a new grader-score row.</summary>
    /// <param name="record">The row to persist. Its <see cref="GraderScoreRecord.Id"/> is ignored.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertAsync(GraderScoreRecord record, CancellationToken cancellationToken = default);

    /// <summary>Returns the total number of rows in <c>grader_scores</c>.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> GetRowCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes the oldest <paramref name="count"/> rows, enforcing <see cref="JudgeOptions.GraderScoreMaxRows"/>.</summary>
    /// <param name="count">The number of oldest rows to delete.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all rows where <c>created_at_utc &lt; <paramref name="cutoff"/></c>, enforcing
    /// <see cref="JudgeOptions.GraderScoreRetentionDays"/>.
    /// </summary>
    /// <param name="cutoff">The exclusive UTC timestamp cutoff; rows older than this are deleted.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every row currently in <c>grader_scores</c>, for <see cref="IGraderReliabilityAnalyzer"/>'s
    /// offline statistics. Read-only and safe to call at any time; never mutates the table.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<IReadOnlyList<GraderScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default);
}
