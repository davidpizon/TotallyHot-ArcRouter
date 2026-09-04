namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// One comparison policy under regret evaluation (research-doc Table 4) — evaluation-only, independent
/// of the production <see cref="Router.IRoutingPolicy"/> interface a live proxy could register
/// (docs/router/regret-evaluation-harness-plan.md "Baselines"). Implementations must be
/// stateless-or-online: no I/O, so <see cref="Route"/> is synchronous.
/// </summary>
public interface IRegretBaselineRouter
{
    /// <summary>Gets the name reported in the N5 comparison table (e.g. <c>"always_opus"</c>, <c>"dim_best"</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Picks a model id for <paramref name="context"/>, using only the signals <see cref="RegretReplayContext"/>
    /// exposes — never the task's own outcome row.
    /// </summary>
    /// <param name="context">The task's dimension and candidate pool.</param>
    /// <returns>
    /// A model id drawn from <see cref="RegretReplayContext.CandidateModelIds"/>, or <see langword="null"/>
    /// when this baseline cannot route the task (e.g. its target model was never scored on it) — the
    /// task is then excluded from this baseline's <see cref="RegretReplayResult"/>, not counted as a
    /// zero-reward pick.
    /// </returns>
    string? Route(RegretReplayContext context);
}