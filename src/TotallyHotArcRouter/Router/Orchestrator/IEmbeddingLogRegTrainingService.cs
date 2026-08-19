namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The single guarded entry point that trains and hot-swaps the <c>logreg</c> voter's model artifact
/// (docs/router/live-feedback-learning-plan.md Phase 4c) - shared by the CLI flag, the automatic
/// threshold trigger, and (Phase 5) the Governance retrain button, so all three go through the same
/// gather/blend/validate/write/reload sequence rather than three independent implementations.
/// </summary>
public interface IEmbeddingLogRegTrainingService
{
    /// <summary>
    /// Gathers OOD bootstrap and live memory samples, blends them, and - if enough data is available -
    /// trains, validates, and atomically writes a new model artifact, then signals the <c>logreg</c>
    /// voter to reload it. Never runs concurrently with itself: a call made while another is already in
    /// progress returns immediately with <see cref="LogRegTrainingResultKind.AlreadyRunning"/> rather than
    /// queuing or blocking.
    /// </summary>
    /// <param name="bootstrapProgress">Reports OOD bootstrap embedding progress (tasks embedded so far), or <see langword="null"/> to skip reporting.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The outcome: what was gathered, and whether an artifact was written.</returns>
    Task<LogRegTrainingOutcome> RetrainAsync(IProgress<int>? bootstrapProgress = null, CancellationToken cancellationToken = default);
}
