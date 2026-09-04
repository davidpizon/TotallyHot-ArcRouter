namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// One (task, winning model) training example derived from the CodeRouterBench OOD split by
/// <see cref="LogRegTrainer.LoadOodTrainingExamples"/> — the only split that publishes task text, and the
/// shared loading/labeling logic behind both the <c>LogReg</c> and <c>kNN Retrieval</c> comparison
/// baselines (docs/router/regret-evaluation-harness-plan.md's N4).
/// </summary>
/// <param name="TaskId">The corpus's <c>task_id</c>.</param>
/// <param name="Text">The task's prompt text, extracted from its raw JSON.</param>
/// <param name="Label">The canonicalized model id that resolved the task most cheaply.</param>
public sealed record OodTrainingExample(string TaskId, string Text, string Label);