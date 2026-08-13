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
    Failed,
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
public sealed record BenchmarkSyncProgress(
    string FileName,
    BenchmarkSyncStage Stage,
    long? BytesTransferred = null,
    int? RowsImported = null);

/// <summary>The terminal outcome of syncing one file.</summary>
/// <param name="FileName">The file that was synced.</param>
/// <param name="Succeeded">Whether the file's checksum verified, parsed, and imported successfully.</param>
/// <param name="RowCount">The number of rows imported, when <paramref name="Succeeded"/> is <see langword="true"/>.</param>
/// <param name="ErrorMessage">A human-readable failure reason, when <paramref name="Succeeded"/> is <see langword="false"/>.</param>
public sealed record BenchmarkFileSyncOutcome(string FileName, bool Succeeded, int? RowCount, string? ErrorMessage);

/// <summary>The aggregate result of one <see cref="BenchmarkSyncService.SyncAsync"/> call.</summary>
/// <param name="RepoCommit">The dataset commit the sync resolved against.</param>
/// <param name="Files">Every file's individual outcome, one per <see cref="BenchmarkFileSpec.All"/> entry.</param>
public sealed record BenchmarkSyncResult(string RepoCommit, IReadOnlyList<BenchmarkFileSyncOutcome> Files);
