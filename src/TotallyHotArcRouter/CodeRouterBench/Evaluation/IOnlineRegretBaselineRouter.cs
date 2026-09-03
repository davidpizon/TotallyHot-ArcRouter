namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// An <see cref="IRegretBaselineRouter"/> that updates its own internal state from the reward it actually
/// observes — the online per-arm posterior update N3 (docs/router/regret-evaluation-harness-plan.md)
/// requires for LinUCB/LinTS. <see cref="RegretReplayEngine"/> calls <see cref="Update"/> once per task,
/// immediately after <see cref="IRegretBaselineRouter.Route"/> commits to a model and only with that
/// model's own outcome cell — never a candidate the router did not pick — so the "no leakage" property
/// holds for online baselines exactly as it does for stateless ones.
/// </summary>
public interface IOnlineRegretBaselineRouter : IRegretBaselineRouter
{
    /// <summary>
    /// Folds the observed reward for the arm this router just picked into its internal posterior.
    /// </summary>
    /// <param name="context">The same context passed to the preceding <see cref="IRegretBaselineRouter.Route"/> call.</param>
    /// <param name="selectedModelId">
    /// The model id <see cref="IRegretBaselineRouter.Route"/> returned for
    /// <paramref name="context"/>.
    /// </param>
    /// <param name="reward">
    /// The canonical reward (<see cref="RewardWeights.Reward"/>) of <paramref name="selectedModelId"/>'s
    /// own outcome cell for this task — the same reward every other baseline's <see cref="RegretReplayResult"/>
    /// scores it under.
    /// </param>
    void Update(RegretReplayContext context, string selectedModelId, double reward);
}