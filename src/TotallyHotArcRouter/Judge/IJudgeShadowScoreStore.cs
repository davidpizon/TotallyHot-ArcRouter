namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Persists <see cref="JudgeShadowScoreRecord"/> rows for the shadow judge
/// (docs/router/geval-shadow-scoring-plan.md §1d), and supports the retention purge
/// (<see cref="ScoreTableRetentionService"/>). The table is not merged with <c>grader_scores</c>.
/// </summary>
public interface IJudgeShadowScoreStore : IScoreRetentionStore
{
    /// <summary>Persists a new shadow-score row.</summary>
    /// <param name="record">The row to persist. Its <see cref="JudgeShadowScoreRecord.Id"/> is ignored.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertAsync(JudgeShadowScoreRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every row currently in <c>judge_shadow_scores</c>, oldest first, for
    /// <see cref="IJudgeCalibrationAnalyzer"/>'s Phase G2 report.
    /// </summary>
    /// <remarks>
    /// Unpaged on purpose, mirroring <see cref="IGraderScoreStore.GetAllAsync"/>: the table is bounded by
    /// <see cref="JudgeOptions.MaxRows"/> and <see cref="JudgeOptions.RetentionDays"/>, and the analysis is
    /// a whole-table group-and-correlate that has no meaningful partial answer - a correlation over an
    /// arbitrary page is not a partial correlation, it is a wrong one.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Every persisted row, or an empty list when the table is empty.</returns>
    Task<IReadOnlyList<JudgeShadowScoreRecord>> GetAllAsync(CancellationToken cancellationToken = default);
}