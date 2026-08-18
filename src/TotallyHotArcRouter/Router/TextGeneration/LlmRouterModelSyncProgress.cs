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
/// <param name="TotalBytes">The file's published size in bytes, from the checksum probe, when known.</param>
public sealed record LlmRouterModelSyncProgress(
    string FileName,
    LlmRouterModelSyncStage Stage,
    long? BytesTransferred = null,
    long? TotalBytes = null);

/// <summary>One file a sync is about to download, from the up-front <see cref="LlmRouterModelSyncPlan"/>.</summary>
/// <param name="FileName">The file's name.</param>
/// <param name="SizeBytes">The file's published size in bytes.</param>
public sealed record LlmRouterModelSyncPlanFile(string FileName, long SizeBytes);

/// <summary>
/// The sync's plan, reported once before any file downloads: which files are stale and how many bytes
/// the whole run will transfer, so the panel's cumulative progress bar has a stable denominator from
/// the first byte rather than one that grows as each file starts.
/// </summary>
/// <param name="Files">The files that will be downloaded. A file omitted here was already current.</param>
/// <param name="TotalBytes">The combined published size of every file in <paramref name="Files"/>.</param>
public sealed record LlmRouterModelSyncPlan(IReadOnlyList<LlmRouterModelSyncPlanFile> Files, long TotalBytes);

/// <summary>One file's final outcome after a sync attempt.</summary>
/// <param name="FileName">The file this outcome is about.</param>
/// <param name="Succeeded">Whether the file is present and usable in the cache directory after this sync.</param>
/// <param name="ChecksumVerified">Whether <paramref name="Succeeded"/> was confirmed against a published checksum, as opposed to presence-only.</param>
/// <param name="ErrorMessage">The failure reason when <paramref name="Succeeded"/> is <see langword="false"/>; otherwise <see langword="null"/>.</param>
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
