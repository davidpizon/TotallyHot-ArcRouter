namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The single guarded entry point that trains and atomically writes the self-organizing cluster model's
/// artifact (docs/router/self-organizing-classification-plan.md Phase T2) - shared by the CLI flag, the
/// automatic threshold trigger, and (Phase T5) the Governance retrain button, so all three go through the
/// same gather/blend/train/validate/write sequence rather than three independent implementations.
/// </summary>
public interface IClusterTrainingService
{
    /// <summary>
    /// Gathers OOD bootstrap and live memory samples, blends them, and - if enough data is available -
    /// sweeps <c>k</c>, trains, validates, and atomically writes a new cluster model artifact. Never runs
    /// concurrently with itself: a call made while another is already in progress returns immediately with
    /// <see cref="ClusterTrainingResultKind.AlreadyRunning"/> rather than queuing or blocking.
    /// </summary>
    /// <param name="bootstrapProgress">
    /// Reports OOD bootstrap embedding progress (tasks embedded so far), or
    /// <see langword="null"/> to skip reporting.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The outcome: what was gathered, and whether an artifact was written.</returns>
    Task<ClusterTrainingOutcome> RetrainAsync(IProgress<int>? bootstrapProgress = null,
        CancellationToken cancellationToken = default);
}