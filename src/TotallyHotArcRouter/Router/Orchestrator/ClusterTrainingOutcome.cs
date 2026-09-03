namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>The result category of one <see cref="IClusterTrainingService.RetrainAsync"/> call.</summary>
public enum ClusterTrainingResultKind
{
    /// <summary>A new artifact was trained, validated, and written.</summary>
    Trained,

    /// <summary>Too few training samples were available; the prior artifact (if any) is untouched.</summary>
    Declined,

    /// <summary>A retrain was already in progress; this call was skipped rather than queued.</summary>
    AlreadyRunning
}

/// <summary>
/// The outcome of one <see cref="IClusterTrainingService.RetrainAsync"/> call
/// (docs/router/self-organizing-classification-plan.md Phase T2) - an audit-trail record of what a
/// retrain did or declined to do, and why.
/// </summary>
/// <param name="Kind">The result category.</param>
/// <param name="Message">A human-readable explanation, suitable for a log line or an admin surface.</param>
/// <param name="BootstrapTaskCount">The number of OOD bootstrap tasks gathered, regardless of outcome.</param>
/// <param name="MemoryEntryCount">The number of live memory entries gathered, regardless of outcome.</param>
/// <param name="SampleCount">
/// The total number of training samples gathered (bootstrap tasks plus live memory entries),
/// regardless of outcome.
/// </param>
/// <param name="ChosenK">The number of clusters the k-sweep selected, or 0 if the retrain declined.</param>
public sealed record ClusterTrainingOutcome(
    ClusterTrainingResultKind Kind,
    string Message,
    int BootstrapTaskCount,
    int MemoryEntryCount,
    int SampleCount,
    int ChosenK);