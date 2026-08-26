namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The offline, streaming replay loop (docs/router/regret-evaluation-harness-plan.md "Replay engine") —
/// no live API calls. Iterates a fixed sequence of task outcomes, asks the router under test to pick a
/// model from each task's own candidate pool without ever showing it that task's outcome row first, then
/// folds the pick into a shared <see cref="RegretReplayResult"/>.
/// </summary>
public static class RegretReplayEngine
{
    /// <summary>
    /// Replays <paramref name="tasks"/> against <paramref name="router"/> under <paramref name="weights"/>.
    /// </summary>
    /// <param name="tasks">
    /// The task outcomes to replay, in the order they should be presented to <paramref name="router"/> —
    /// callers needing the corpus's own deterministic order should sort before calling.
    /// </param>
    /// <param name="router">The baseline or Orchestrator arm under test.</param>
    /// <param name="weights">The reward weights, the same instance every router in a comparison should use.</param>
    public static RegretReplayResult Replay(IEnumerable<RegretTaskOutcome> tasks, IRegretBaselineRouter router, RewardWeights weights)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(weights);

        var result = new RegretReplayResult { RouterName = router.Name };

        foreach (var task in tasks)
        {
            // The router sees only dimension + candidate ids + (when published) task text - never
            // task.Cells - enforcing "no leakage" at the call boundary rather than trusting each
            // IRegretBaselineRouter implementation to police itself.
            var context = new RegretReplayContext(task.TaskId, task.Dimension, [.. task.Cells.Keys], task.TaskText);
            var selectedModelId = router.Route(context);

            // Online baselines (LinUCB/LinTS) get fed back exactly the reward of the arm they picked -
            // never another candidate's cell - so the same no-leakage property holds for the update path.
            if (selectedModelId is not null && router is IOnlineRegretBaselineRouter online
                && task.Cells.TryGetValue(selectedModelId, out var selectedCell))
            {
                online.Update(context, selectedModelId, weights.Reward(selectedCell));
            }

            result.Record(task, selectedModelId, weights);
        }

        return result;
    }
}
