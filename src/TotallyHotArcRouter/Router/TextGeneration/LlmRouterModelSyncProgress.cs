namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>One file's sync stage, mirroring <see cref="CodeRouterBench.BenchmarkSyncStage"/>'s shape minus the Importing stage - this pipeline has no import step.</summary>
public enum LlmRouterModelSyncStage
{
    /// <summary>Bytes are being downloaded from the model's base URL.</summary>
    Downloading,

    /// <summary>The downloaded bytes' checksum is being compared to the published value, when available.</summary>
    Verifying,

    /// <summary>The file's sync completed successfully.</summary>
    Completed,

    /// <summary>The file's sync failed; any previously cached copy of this file is untouched.</summary>
    Failed,
}

/// <summary>One file's progress update during a sync.</summary>
/// <param name="FileName">The file this update is about.</param>
/// <param name="Stage">The stage the file is currently in.</param>
/// <param name="BytesTransferred">Bytes downloaded so far, when known.</param>
public sealed record LlmRouterModelSyncProgress(
    string FileName,
    LlmRouterModelSyncStage Stage,
    long? BytesTransferred = null);

/// <summary>One file's final outcome after a sync attempt.</summary>
public sealed record LlmRouterModelFileSyncOutcome(
    string FileName,
    bool Succeeded,
    bool ChecksumVerified,
    string? ErrorMessage);

/// <summary>The aggregate result of one <see cref="LlmRouterModelSyncService.SyncAsync"/> call.</summary>
/// <param name="BaseUrl">The base URL the sync ran against (the active override at the time the sync started).</param>
/// <param name="Files">Every file's individual outcome, one per <see cref="LlmRouterModelFiles.All"/> entry.</param>
public sealed record LlmRouterModelSyncResult(string BaseUrl, IReadOnlyList<LlmRouterModelFileSyncOutcome> Files);

/// <summary>
/// The most recent checksum-verification outcome recorded by <see cref="LlmRouterModelSyncService"/>,
/// paired with the base URL it was for so a caller can tell whether it still applies to the
/// currently-active model.
/// </summary>
/// <param name="BaseUrl">The base URL the recorded sync ran against.</param>
/// <param name="Files">Whether each file was checksum-verified during that sync, keyed by file name.</param>
public sealed record LlmRouterModelVerificationSnapshot(string BaseUrl, IReadOnlyDictionary<string, bool> Files);
