using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// One task's full outcome row — every model the corpus scored on this task, keyed by
/// <see cref="ModelNameCanonicalizer.Canonicalize(string, string?)"/> so a baseline's chosen model id
/// only needs to match a candidate id handed to it in <see cref="RegretReplayContext.CandidateModelIds"/>,
/// never re-derive canonicalization itself.
/// </summary>
/// <param name="TaskId">The corpus's <c>task_id</c>.</param>
/// <param name="Dimension">The task's dimension, as published by the corpus.</param>
/// <param name="Cells">
/// Every model scored on this task, keyed by canonical model id. Per
/// docs/router/regret-evaluation-harness-plan.md's candidate-derivation decision, this set — not a
/// fixed global roster — is the candidate pool a baseline may choose from for this task.
/// </param>
/// <param name="TaskText">
/// The task's prompt text, when the source split publishes one — only the OOD split does (N4's
/// text-availability constraint, the same one <see cref="LogRegTrainer"/> works around). <see langword="null"/>
/// for every ID-test/probing task, which is what lets the text-limited baselines (<see cref="LogRegBaseline"/>,
/// <see cref="KnnRetrievalBaseline"/>) report "not computable" on those splits rather than silently
/// substituting something else.
/// </param>
public sealed record RegretTaskOutcome(
    string TaskId,
    string Dimension,
    IReadOnlyDictionary<string, RegretOutcomeCell> Cells,
    string? TaskText = null);