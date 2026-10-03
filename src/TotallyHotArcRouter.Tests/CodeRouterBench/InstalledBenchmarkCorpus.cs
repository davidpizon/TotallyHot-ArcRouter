using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// Gives the CodeRouterBench reconciliation tests a private copy of the real, installed corpus. Those
/// tests exist to check computed results against the synced data and skip themselves when it is absent,
/// so pointing them at <see cref="TestAppDataDirectory"/>'s empty scratch directory would turn them into
/// permanent skips. This is the one place the suite reads from the real data directory, and it only reads.
/// </summary>
/// <remarks>
/// <para>
/// A copy, not the installed file itself: the corpus runs in SQLite's WAL mode, so even a read-only query
/// against the real file creates and deletes <c>-wal</c>/<c>-shm</c> sidecars beside it. Copying the bytes
/// with a shared read touches nothing there. It also keeps a running router's open handle from turning
/// into lock contention mid-test.
/// </para>
/// <para>
/// The location comes from <see cref="AppDataPaths.MachineWideCandidate"/> and
/// <see cref="AppDataPaths.PerUserCandidate"/>, both pure path computations, rather than from
/// <see cref="AppDataPaths.ResolveMachineSharedDirectory"/>. Real resolution write-probes the
/// machine-wide directory, and under test it returns the scratch directory anyway.
/// </para>
/// </remarks>
internal static class InstalledBenchmarkCorpus
{
    private const string FileName = "coderouterbench.db";

    // Committed rows the router hasn't checkpointed into the main file yet live only in this sidecar.
    private const string WriteAheadLogSuffix = "-wal";

    /// <summary>
    /// Copies the first installed corpus found - machine-wide before per-user, the order the router
    /// itself resolves them in - into a fresh directory under <see cref="TestAppDataDirectory.Root"/> and
    /// returns a <see cref="BenchmarkDatabase"/> over the copy. When there is no installed corpus, or it
    /// can't be copied, the returned database's file doesn't exist, which the caller's own existence check
    /// reports as "not synced". The copy is left for <see cref="TestTempDirectorySweeper"/> to delete at the
    /// end of the run, after it has released SQLite's pooled handles.
    /// </summary>
    public static BenchmarkDatabase Open()
    {
        var copyDirectory = Path.Combine(path1: TestAppDataDirectory.Root, path2: "installed-corpus",
            path3: Guid.NewGuid().ToString("N"));
        var copyPath = Path.Combine(path1: copyDirectory, path2: FileName);

        var installed = new[] { AppDataPaths.MachineWideCandidate(), AppDataPaths.PerUserCandidate() }
            .Select(directory => Path.Combine(path1: directory, path2: FileName))
            .FirstOrDefault(File.Exists);
        if (installed is not null) TryCopy(source: installed, destination: copyPath);

        return new BenchmarkDatabase(Options.Create(new StorageOptions { BenchmarkDatabasePath = copyPath }));
    }

    /// <summary>
    /// Copies <paramref name="source"/>, and its write-ahead log when one exists, to
    /// <paramref name="destination"/>. On any failure it removes whatever it copied, so a half-copied
    /// corpus reads as absent instead of as data missing its newest rows.
    /// </summary>
    private static void TryCopy(string source, string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        try
        {
            Directory.CreateDirectory(directory);
            CopyShared(source: source, destination: destination);

            var writeAheadLog = source + WriteAheadLogSuffix;
            if (File.Exists(writeAheadLog))
                CopyShared(source: writeAheadLog, destination: destination + WriteAheadLogSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                Directory.Delete(path: directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort; the end-of-run sweep gets another chance at it.
            }
        }
    }

    /// <summary>
    /// Copies one file, opening the source with explicit read/write/delete sharing so a router that already
    /// holds it open for writing can't turn the copy into a sharing violation.
    /// </summary>
    private static void CopyShared(string source, string destination)
    {
        using var input = new FileStream(path: source, mode: FileMode.Open, access: FileAccess.Read,
            share: FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(path: destination, mode: FileMode.CreateNew, access: FileAccess.Write);
        input.CopyTo(output);
    }
}
