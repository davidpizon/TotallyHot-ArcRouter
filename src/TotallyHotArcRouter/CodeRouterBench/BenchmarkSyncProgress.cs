namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>The stage of one file's sync, reported via <see cref="BenchmarkSyncProgress"/>.</summary>
public enum BenchmarkSyncStage
{
    /// <summary>Bytes are being downloaded from Hugging Face.</summary>
    Downloading,

    /// <summary>The downloaded bytes' git blob SHA-1 is being compared to the published checksum.</summary>
    Verifying,

    /// <summary>The verified bytes are being parsed and written to the database.</summary>
    Importing,

    /// <summary>The file's sync completed successfully.</summary>
    Completed,

    /// <summary>The file's sync failed; its prior table rows and ledger entry are untouched.</summary>
    Failed
}

/// <summary>
/// One progress update during <see cref="BenchmarkSyncService.SyncAsync"/>, reported through the
/// caller's <see cref="IProgress{T}"/> so Phase 4's gRPC stream can show per-file progress rather than
/// sitting silent through a multi-megabyte download.
/// </summary>
/// <param name="FileName">The file this update is about.</param>
/// <param name="Stage">The stage the file is currently in.</param>
/// <param name="BytesTransferred">Bytes downloaded so far, when known.</param>
/// <param name="RowsImported">Rows imported, once <see cref="BenchmarkSyncStage.Completed"/> is reached.</param>
/// <param name="TotalBytes">
/// The file's published size in bytes, from the checksum probe. Set on every update for a file that is
/// actually being synced, so a progress bar has a stable denominator without a separate lookup.
/// </param>
public sealed record BenchmarkSyncProgress(
    string FileName,
    BenchmarkSyncStage Stage,
    long? BytesTransferred = null,
    int? RowsImported = null,
    long? TotalBytes = null);

/// <summary>One file <see cref="BenchmarkSyncService.SyncAsync"/> will download, from its up-front plan.</summary>
/// <param name="FileName">The file's name in the published Hugging Face dataset.</param>
/// <param name="SizeBytes">The file's published size in bytes.</param>
public sealed record BenchmarkSyncPlanFile(string FileName, long SizeBytes);

/// <summary>
/// The set of files a <see cref="BenchmarkSyncService.SyncAsync"/> call will actually download - those
/// whose ledger checksum does not match the just-fetched published one - computed once up front and
/// reported before any file starts, so a cumulative progress bar has a stable denominator from the
/// first byte rather than one that grows as each file starts.
/// </summary>
/// <param name="Files">The stale files that will be downloaded. A file omitted here already matched.</param>
/// <param name="TotalBytes">The combined published size of every file in <see cref="Files"/>.</param>
public sealed record BenchmarkSyncPlan(IReadOnlyList<BenchmarkSyncPlanFile> Files, long TotalBytes);

/// <summary>The terminal outcome of syncing one file.</summary>
/// <param name="FileName">The file that was synced.</param>
/// <param name="Succeeded">Whether the file's checksum verified, parsed, and imported successfully.</param>
/// <param name="RowCount">The number of rows imported, when <paramref name="Succeeded"/> is <see langword="true"/>.</param>
/// <param name="ErrorMessage">
/// A human-readable failure reason, when <paramref name="Succeeded"/> is
/// <see langword="false"/>.
/// </param>
/// <param name="Skipped">
/// Whether this file was never downloaded because its ledger checksum already matched the published
/// one. A skipped file is always <paramref name="Succeeded"/> with its prior <paramref name="RowCount"/>;
/// the flag exists solely so callers (like the gRPC service's per-file error stream) can tell an
/// already-current file apart from one that actually re-synced.
/// </param>
public sealed record BenchmarkFileSyncOutcome(
    string FileName,
    bool Succeeded,
    int? RowCount,
    string? ErrorMessage,
    bool Skipped = false);

/// <summary>The aggregate result of one <see cref="BenchmarkSyncService.SyncAsync"/> call.</summary>
/// <param name="RepoCommit">The dataset commit the sync resolved against.</param>
/// <param name="Files">Every file's individual outcome, one per <see cref="BenchmarkFileSpec.All"/> entry.</param>
public sealed record BenchmarkSyncResult(string RepoCommit, IReadOnlyList<BenchmarkFileSyncOutcome> Files);