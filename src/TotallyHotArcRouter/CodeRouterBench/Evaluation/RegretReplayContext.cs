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
public sealed record RegretReplayContext(string TaskId, string Dimension, IReadOnlyList<string> CandidateModelIds);
