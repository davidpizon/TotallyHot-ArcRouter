namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The shared metrics accumulator every baseline and the Orchestrator arm report through
/// (docs/router/regret-evaluation-harness-plan.md "Metrics") — <c>CumReg</c>, <c>AvgPerf</c>,
/// <c>TotTok</c>, <c>$Total</c>, and <c>Perf/$</c>, computed identically for every router under test so
/// their numbers are directly comparable.
/// </summary>
public sealed class RegretReplayResult
{
    private double _cumulativeRegret;
    private double _totalScore;
    private double _totalCostUsd;
    private long _totalTokens;
    private int _scoredTaskCount;
    private int _skippedTaskCount;

    /// <summary>Gets the name of the router this result was accumulated for.</summary>
    public required string RouterName { get; init; }

    /// <summary>
    /// Gets <c>CumReg_N = Σ_i (r*_i − r_i(a_i))</c> — the sum of per-task regret against the per-task
    /// oracle, over every task this router was scored on. Not a gap to a single best-arm policy.
    /// </summary>
    public double CumulativeRegret => _cumulativeRegret;

    /// <summary>Gets <c>AvgPerf</c> — the mean verifier score <c>s_i(a_i)</c> of the model actually selected.</summary>
    public double AvgPerf => _scoredTaskCount == 0 ? 0d : _totalScore / _scoredTaskCount;

    /// <summary>Gets <c>TotTok</c> — the sum of total tokens consumed by the model actually selected, each task.</summary>
    public long TotalTokens => _totalTokens;

    /// <summary>Gets <c>$Total</c> — the sum of cost in USD of the model actually selected, each task.</summary>
    public double TotalCostUsd => _totalCostUsd;

    /// <summary>
    /// Gets <c>Perf/$</c> — <c>AvgPerf</c> expressed as a percentage divided by <c>$Total</c>, or
    /// <see langword="null"/> when <see cref="TotalCostUsd"/> is zero (nothing was spent, so the ratio
    /// is undefined rather than infinite).
    /// </summary>
    public double? PerfPerDollar => TotalCostUsd == 0d ? null : (AvgPerf * 100d) / TotalCostUsd;

    /// <summary>Gets the number of tasks this router actually routed and was scored on.</summary>
    public int ScoredTaskCount => _scoredTaskCount;

    /// <summary>
    /// Gets the number of tasks this router could not route (<see cref="IRegretBaselineRouter.Route"/>
    /// returned <see langword="null"/>) and that were therefore excluded from every other metric here.
    /// </summary>
    public int SkippedTaskCount => _skippedTaskCount;

    /// <summary>
    /// Accumulates one task's outcome: the oracle reward over every candidate in
    /// <paramref name="outcome"/>, and — when <paramref name="selectedModelId"/> is non-null — the
    /// reward the router under test actually achieved, folded into every metric above.
    /// </summary>
    /// <param name="outcome">The task's full outcome row (every model the corpus scored, independent of what the router could see).</param>
    /// <param name="selectedModelId">
    /// The canonical model id the router picked for this task, or <see langword="null"/> when it could
    /// not route the task — the task is then counted in <see cref="SkippedTaskCount"/> and excluded from
    /// every other metric, per <see cref="IRegretBaselineRouter.Route"/>'s contract.
    /// </param>
    /// <param name="weights">The reward weights <c>(ε1, ε2)</c> — the same instance for every router under comparison.</param>
    public void Record(RegretTaskOutcome outcome, string? selectedModelId, RewardWeights weights)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(weights);

        if (outcome.Cells.Count == 0)
        {
            throw new ArgumentException("A task outcome must have at least one cell to compute an oracle from.", nameof(outcome));
        }

        if (selectedModelId is null)
        {
            _skippedTaskCount++;
            return;
        }

        if (!outcome.Cells.TryGetValue(selectedModelId, out var selectedCell))
        {
            throw new ArgumentException(
                $"Selected model '{selectedModelId}' has no outcome cell for task '{outcome.TaskId}' — a router must only return an id from RegretReplayContext.CandidateModelIds.",
                nameof(selectedModelId));
        }

        var oracleReward = outcome.Cells.Values.Max(weights.Reward);
        var selectedReward = weights.Reward(selectedCell);

        _cumulativeRegret += oracleReward - selectedReward;
        _totalScore += selectedCell.Score;
        _totalCostUsd += selectedCell.CostUsd;
        _totalTokens += selectedCell.TotalTokens ?? 0L;
        _scoredTaskCount++;
    }
}
