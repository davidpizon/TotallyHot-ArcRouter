namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// One task's entry in a <see cref="KnnRetrievalArtifact"/>'s frozen index: the task's embedding and the
/// model that resolved it most cheaply, per <see cref="LogRegTrainer.LoadOodTrainingExamples"/>'s labeling
/// rule (the same rule <see cref="LogRegBaseline"/>'s training labels use).
/// </summary>
/// <param name="TaskId">
/// The corpus's <c>task_id</c> — how <see cref="KnnRetrievalBaseline.Route"/> finds a query task's
/// own entry for leave-one-out retrieval.
/// </param>
/// <param name="Embedding">
/// The task's prompt-text embedding, unit-normalized per <see cref="Router.Embeddings.EmbeddingResult"/>'s
/// convention so cosine similarity between two entries reduces to a plain dot product.
/// </param>
/// <param name="Label">The canonicalized model id that resolved the task most cheaply.</param>
public sealed record KnnRetrievalEntry(string TaskId, IReadOnlyList<float> Embedding, string Label);