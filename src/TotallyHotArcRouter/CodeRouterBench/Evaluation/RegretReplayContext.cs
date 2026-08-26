namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// What an <see cref="IRegretBaselineRouter"/> is allowed to see for one task — never the task's own
/// outcome row (docs/router/regret-evaluation-harness-plan.md's "no leakage" correctness property).
/// </summary>
/// <param name="TaskId">The corpus's <c>task_id</c>, for baselines that key state per task (none in N1/N2).</param>
/// <param name="Dimension">The task's dimension.</param>
/// <param name="CandidateModelIds">
/// Every model id this task's outcome row actually has a cell for, already canonicalized
/// (<see cref="RegretTaskOutcome.Cells"/>'s keys) — the only ids a baseline may legally return from
/// <see cref="IRegretBaselineRouter.Route"/>.
/// </param>
/// <param name="TaskText">
/// The task's prompt text, forwarded from <see cref="RegretTaskOutcome.TaskText"/> — never derived from
/// <see cref="RegretTaskOutcome.Cells"/>, so exposing it here carries no outcome-row leakage.
/// <see langword="null"/> on every split that publishes no task text (everything but OOD), which the
/// text-limited baselines (<see cref="LogRegBaseline"/>) read as "route nothing on this task."
/// </param>
public sealed record RegretReplayContext(
    string TaskId,
    string Dimension,
    IReadOnlyList<string> CandidateModelIds,
    string? TaskText = null);
