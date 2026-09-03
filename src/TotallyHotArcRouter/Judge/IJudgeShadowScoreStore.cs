namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Persists <see cref="JudgeShadowScoreRecord"/> rows for the shadow judge
/// (docs/router/geval-shadow-scoring-plan.md §1d), and supports the retention purge
/// (<see cref="JudgeShadowScoreRetentionService"/>).
/// </summary>
public interface IJudgeShadowScoreStore
{
    /// <summary>Persists a new shadow-score row.</summary>
    /// <param name="record">The row to persist. Its <see cref="JudgeShadowScoreRecord.Id"/> is ignored.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertAsync(JudgeShadowScoreRecord record, CancellationToken cancellationToken = default);

    /// <summary>Returns the total number of rows in <c>judge_shadow_scores</c>.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> GetRowCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes the oldest <paramref name="count"/> rows, enforcing <see cref="JudgeOptions.MaxRows"/>.</summary>
    /// <param name="count">The number of oldest rows to delete.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all rows where <c>created_at_utc &lt; <paramref name="cutoff"/></c>, enforcing
    /// <see cref="JudgeOptions.RetentionDays"/>.
    /// </summary>
    /// <param name="cutoff">The exclusive UTC timestamp cutoff; rows older than this are deleted.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows actually deleted.</returns>
    Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}