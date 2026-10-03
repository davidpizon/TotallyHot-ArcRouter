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

    // The WAL header: magic, format version, page size, checkpoint sequence, two salts, two checksums.
    private const int WriteAheadLogHeaderLength = 32;

    // A router rarely resets its WAL at all, so one retry almost always suffices; after that the corpus
    // reads as absent rather than the tests spinning.
    private const int MaxCopyAttempts = 3;

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
    /// <remarks>
    /// The two files are copied at different instants while a running router may still write them, so the
    /// pair is only kept when the WAL's header is the same just before the database copy and just after
    /// the WAL copy. The header holds the checkpoint sequence and the salts that tag every valid frame, and
    /// SQLite rewrites it whenever the WAL is created, reset or truncated. Those are the only changes that
    /// can pair a database image with frames it doesn't match: a checkpoint that leaves the WAL in place
    /// keeps its frames there, and a frame half-written at the end of the copied WAL fails its checksum and
    /// is ignored. Reading the header uses the same shared read as the copy, so this still touches nothing
    /// in the real directory, which is why it is used instead of SQLite's online backup API: that would
    /// open the real file and create its <c>-shm</c> sidecar.
    /// </remarks>
    private static void TryCopy(string source, string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var writeAheadLog = source + WriteAheadLogSuffix;
        try
        {
            for (var attempt = 1; attempt <= MaxCopyAttempts; attempt++)
            {
                if (Directory.Exists(directory)) Directory.Delete(path: directory, recursive: true);
                Directory.CreateDirectory(directory);

                var headerBefore = ReadWriteAheadLogHeader(writeAheadLog);
                CopyShared(source: source, destination: destination);
                if (headerBefore is not null)
                    CopyShared(source: writeAheadLog, destination: destination + WriteAheadLogSuffix);
                var headerAfter = ReadWriteAheadLogHeader(writeAheadLog);

                if (headerBefore is null ? headerAfter is null : headerAfter is not null && headerBefore.SequenceEqual(headerAfter))
                    return;
            }

            throw new IOException($"'{source}' kept changing while it was copied.");
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
    /// Reads the first <see cref="WriteAheadLogHeaderLength"/> bytes of <paramref name="path"/> with a
    /// shared read, fewer when the file is shorter, or returns <see langword="null"/> when it does not exist.
    /// </summary>
    private static byte[]? ReadWriteAheadLogHeader(string path)
    {
        try
        {
            using var input = new FileStream(path: path, mode: FileMode.Open, access: FileAccess.Read,
                share: FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[WriteAheadLogHeaderLength];
            var read = input.ReadAtLeast(buffer: header, minimumBytes: header.Length, throwOnEndOfStream: false);
            return header[..read];
        }
        catch (FileNotFoundException)
        {
            return null;
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
