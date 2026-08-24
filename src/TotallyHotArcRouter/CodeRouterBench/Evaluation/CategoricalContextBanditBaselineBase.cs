namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Shared per-arm, per-dimension posterior bookkeeping for the categorical-context bandit baselines
/// (research-doc Table 4's LinUCB/LinTS row) — the only context <see cref="RegretReplayContext"/> exposes
/// on the ID-test split is <see cref="RegretReplayContext.Dimension"/>, so the context vector every
/// subclass scores against is a pure one-hot indicator over dimension, one indicator per candidate model.
/// </summary>
/// <remarks>
/// Because the context is always a pure one-hot vector, the general LinUCB/LinTS ridge-regression matrix
/// <c>A = λI + Σ x·xᵀ</c> is diagonal by construction — a pull with dimension <c>d</c> only ever touches
/// <c>A</c>'s <c>(d,d)</c> entry — so this never needs a matrix inverse: <c>A⁻¹_dd = 1 / (λ + n_d)</c>
/// where <c>n_d</c> is the pull count for that (arm, dimension) pair, and the posterior mean is
/// <c>b_d / (λ + n_d)</c> where <c>b_d</c> is the reward sum for the same pair. Subclasses implement only
/// the score each algorithm derives from that (mean, confidence-denominator) pair — the UCB bonus for
/// <see cref="LinUcbBaseline"/>, a Gaussian posterior draw for <see cref="LinThompsonSamplingBaseline"/>.
/// </remarks>
public abstract class CategoricalContextBanditBaselineBase : IOnlineRegretBaselineRouter
{
    private readonly double _lambda;
    private readonly Dictionary<string, Dictionary<string, ArmDimensionStat>> _stats = new(StringComparer.Ordinal);

    /// <summary>Initializes the shared ridge prior.</summary>
    /// <param name="lambda">λ, the ridge prior weight added to every (arm, dimension) pair's pull count.</param>
    protected CategoricalContextBanditBaselineBase(double lambda) => _lambda = lambda;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Ties are broken by ordinal model-id order, the same convention as
    /// <see cref="DimensionBestBaseline"/> and <see cref="Router.Orchestrator.OrchestratorRoutingPolicy"/>.
    /// Never returns <see langword="null"/> — every candidate the context offers has a scoreable (possibly
    /// prior-only) posterior.
    /// </remarks>
    public string? Route(RegretReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? best = null;
        var bestScore = double.NegativeInfinity;

        foreach (var modelId in context.CandidateModelIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            var stat = GetOrAddStat(modelId, context.Dimension);
            var denominator = _lambda + stat.Count;
            var mean = stat.Count == 0 ? 0d : stat.RewardSum / denominator;
            var score = ScoreArm(mean, denominator);

            if (score > bestScore)
            {
                bestScore = score;
                best = modelId;
            }
        }

        return best;
    }

    /// <inheritdoc />
    public void Update(RegretReplayContext context, string selectedModelId, double reward)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedModelId);

        var stat = GetOrAddStat(selectedModelId, context.Dimension);
        stat.Count++;
        stat.RewardSum += reward;
    }

    /// <summary>
    /// Replays <paramref name="probingTasks"/> through this bandit's own <see cref="Route"/>/<see cref="Update"/>
    /// loop before the scored stream begins ("warm-started on the probing set, seed 42" — research-doc
    /// Table 4), seeding its posterior from probing-split outcomes rather than starting cold.
    /// </summary>
    /// <param name="probingTasks">
    /// The probing split's task outcomes, in the order they should be presented — callers needing a fixed
    /// order should sort before calling, matching <see cref="RegretReplayEngine.Replay"/>'s own contract.
    /// </param>
    /// <param name="weights">The same canonical reward weights the scored replay will use.</param>
    public void WarmStart(IEnumerable<RegretTaskOutcome> probingTasks, RewardWeights weights)
    {
        ArgumentNullException.ThrowIfNull(probingTasks);
        ArgumentNullException.ThrowIfNull(weights);

        foreach (var task in probingTasks)
        {
            var context = new RegretReplayContext(task.TaskId, task.Dimension, [.. task.Cells.Keys]);
            var selected = Route(context);

            if (selected is not null && task.Cells.TryGetValue(selected, out var cell))
            {
                Update(context, selected, weights.Reward(cell));
            }
        }
    }

    /// <summary>
    /// Derives this algorithm's arm score from the posterior mean and confidence denominator of one
    /// (arm, dimension) pair.
    /// </summary>
    /// <param name="mean">The posterior mean reward <c>b_d / (λ + n_d)</c>, or <c>0</c> when <c>n_d = 0</c>.</param>
    /// <param name="denominator">The confidence denominator <c>λ + n_d</c> (always positive since λ &gt; 0).</param>
    protected abstract double ScoreArm(double mean, double denominator);

    private ArmDimensionStat GetOrAddStat(string modelId, string dimension)
    {
        if (!_stats.TryGetValue(modelId, out var perDimension))
        {
            perDimension = new Dictionary<string, ArmDimensionStat>(StringComparer.Ordinal);
            _stats[modelId] = perDimension;
        }

        if (!perDimension.TryGetValue(dimension, out var stat))
        {
            stat = new ArmDimensionStat();
            perDimension[dimension] = stat;
        }

        return stat;
    }

    private sealed class ArmDimensionStat
    {
        public int Count;
        public double RewardSum;
    }
}
