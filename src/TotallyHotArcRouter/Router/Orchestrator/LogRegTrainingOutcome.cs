namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>The result category of one <see cref="IEmbeddingLogRegTrainingService.RetrainAsync"/> call.</summary>
public enum LogRegTrainingResultKind
{
    /// <summary>A new artifact was trained, validated, and written; <see cref="LogRegVoter.Reload"/> was signaled.</summary>
    Trained,

    /// <summary>Too few rows or too few distinct models were available; the prior artifact (if any) is untouched.</summary>
    Declined,

    /// <summary>A retrain was already in progress; this call was skipped rather than queued.</summary>
    AlreadyRunning
}

/// <summary>
/// The outcome of one <see cref="IEmbeddingLogRegTrainingService.RetrainAsync"/> call
/// (docs/router/live-feedback-learning-plan.md Phase 4c) - an audit-trail record of what a retrain did or
/// declined to do, and why.
/// </summary>
/// <param name="Kind">The result category.</param>
/// <param name="Message">A human-readable explanation, suitable for a log line or an admin surface.</param>
/// <param name="BootstrapTaskCount">The number of OOD bootstrap tasks gathered, regardless of outcome.</param>
/// <param name="MemoryEntryCount">The number of live memory entries gathered, regardless of outcome.</param>
/// <param name="SampleCount">
/// The total number of training samples gathered (bootstrap rows plus one per live memory
/// entry), regardless of outcome.
/// </param>
/// <param name="ModelsRepresented">The number of distinct models with at least one training sample, regardless of outcome.</param>
public sealed record LogRegTrainingOutcome(
    LogRegTrainingResultKind Kind,
    string Message,
    int BootstrapTaskCount,
    int MemoryEntryCount,
    int SampleCount,
    int ModelsRepresented);