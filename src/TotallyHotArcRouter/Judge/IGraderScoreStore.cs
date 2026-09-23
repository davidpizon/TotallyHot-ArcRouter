namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Persists <see cref="GraderScoreRecord"/> rows for every grader (docs/router/grader-reliability-plan.md,
/// Phase Q4), supports the retention purge (<see cref="ScoreTableRetentionService"/>), and supplies the
/// bulk read <see cref="IGraderReliabilityAnalyzer"/> computes its statistics from. The table is not
/// merged with <c>judge_shadow_scores</c>.
/// </summary>
public interface IGraderScoreStore : IScoreRetentionStore
{
    /// <summary>Persists a new grader-score row.</summary>
    /// <param name="record">The row to persist. Its <see cref="GraderScoreRecord.Id"/> is ignored.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertAsync(GraderScoreRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every row currently in <c>grader_scores</c>, for <see cref="IGraderReliabilityAnalyzer"/>'s
    /// offline statistics. Read-only and safe to call at any time; never mutates the table.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<IReadOnlyList<GraderScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default);
}
