namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// One training example for <see cref="SphericalKMeansTrainer"/>
/// (docs/router/self-organizing-classification-plan.md Phase T2a): a task embedding, its heuristic
/// dimension label (if known), and a relative training weight. Both the OOD bootstrap source
/// (<see cref="OodClusterBootstrapSampleSource"/>) and live <c>memory_entries</c> rows reduce to this same
/// shape, mirroring <see cref="LogRegTrainingSample"/>'s role for the <c>logreg</c> voter.
/// </summary>
/// <param name="Embedding">The task's embedding vector.</param>
/// <param name="Dimension">
/// The heuristic classifier's dimension label for this sample's request, or <see langword="null"/> when
/// unavailable - feeds the artifact's per-cluster heuristic-dimension histogram (Phase T2e).
/// </param>
/// <param name="Weight">
/// The sample's relative weight in centroid computation - live rows are weighted above OOD bootstrap rows
/// via <see cref="Models.RoutingOptions.ClusterLiveSampleWeight"/>, mirroring
/// <see cref="Models.RoutingOptions.LogRegLiveSampleWeight"/>'s role for the <c>logreg</c> voter.
/// </param>
public sealed record ClusterTrainingSample(float[] Embedding, string? Dimension, double Weight);
