namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// One (task embedding, model, observed score) training example for
/// <see cref="EmbeddingLogRegTrainer"/> (docs/router/live-feedback-learning-plan.md Phase 4). Both the
/// OOD bootstrap source and the live <c>memory_entries</c> source produce this same shape, so one trainer
/// serves both - see <see cref="EmbeddingLogRegTrainer"/>'s remarks.
/// </summary>
/// <param name="Embedding">The task's unit-normalized embedding vector.</param>
/// <param name="ModelKey">
/// The example's model, canonicalized via
/// <see cref="Models.ModelNameCanonicalizer.Canonicalize(string,string?)"/> - the same keying convention
/// <see cref="EmbeddingLogRegModelArtifact.ClassWeights"/> uses.
/// </param>
/// <param name="Score">
/// The observed quality score in <c>[0, 1]</c> this example contributes to <paramref name="ModelKey"/>
/// 's regression head.
/// </param>
/// <param name="Weight">
/// The example's relative contribution to the trained gradient, before any per-source blend weight
/// (docs/router/live-feedback-learning-plan.md Phase 4b's live-vs-bootstrap blend) is applied. <c>1.0</c>
/// for an unweighted example.
/// </param>
public sealed record LogRegTrainingSample(float[] Embedding, string ModelKey, double Score, double Weight);