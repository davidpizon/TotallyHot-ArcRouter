namespace TotallyHot.ArcRouter.CodeRouterBench;

/// <summary>
/// A recorded successful sync of one CodeRouterBench source file
/// (docs/router/coderouterbench-sqlite-migration-plan.md) - one row of <c>benchmark_files</c>. Written
/// only once import succeeds; a failed sync leaves the prior row (or its absence) untouched, which is
/// what lets Phase 3's startup probe tell "never synced" apart from "synced, now stale".
/// </summary>
/// <param name="FileName">The published file's name, e.g. <c>id_probing_results_long.csv</c>.</param>
/// <param name="PublishedOid">
/// The git blob SHA-1 that was verified against the downloaded bytes at sync time (never MD5 - see the
/// migration plan's Checksums section).
/// </param>
/// <param name="SizeBytes">The downloaded file's size in bytes, as published by the Hugging Face tree API.</param>
/// <param name="RowCount">The number of data rows imported from this file.</param>
/// <param name="RepoCommit">The resolved <c>X-Repo-Commit</c> of the dataset ref the file was fetched from.</param>
/// <param name="SyncedAtUtc">The UTC timestamp this sync completed.</param>
public sealed record BenchmarkFileLedgerEntry(
    string FileName,
    string PublishedOid,
    long SizeBytes,
    int RowCount,
    string RepoCommit,
    DateTimeOffset SyncedAtUtc);
