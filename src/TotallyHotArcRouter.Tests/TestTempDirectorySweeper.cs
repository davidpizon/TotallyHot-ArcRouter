using Microsoft.Data.Sqlite;

[assembly: AssemblyFixture(typeof(TotallyHot.ArcRouter.Tests.TestTempDirectorySweeper))]

namespace TotallyHot.ArcRouter.Tests;

/// <summary>
/// Keeps <c>%TEMP%/arcrouter-tests</c> - the scratch root roughly forty test classes here create per-test
/// directories under - from growing without bound. Registered as an xUnit assembly fixture, so it is built
/// before the first test in this assembly and disposed after the last.
/// </summary>
/// <remarks>
/// <para>
/// Per-test teardown was never enough on its own, and could not be made enough one class at a time. Almost
/// every class that uses the root does try to delete its directory, but best-effort, swallowing
/// <see cref="IOException"/> - and on Windows that delete loses whenever a pooled SQLite connection still
/// holds the file, which several teardowns never clear. One full run left 136 directories behind; by the
/// time anyone looked there were 30,792, dating back five weeks. A teardown also never runs at all when a
/// run is killed or hangs, which no per-class fix can address.
/// </para>
/// <para>
/// So this sweeps twice. At the end of the run it clears every SQLite pool and deletes what this run
/// created: <see cref="SqliteConnection.ClearAllPools"/> is normally avoided in this suite because, mid-run,
/// it can tear a pooled native handle out from under a parallel test's in-flight query - but at assembly
/// teardown no test is running, so it is safe here and nowhere else. At the start of a run it deletes
/// anything untouched for <see cref="AbandonedAfter"/>, which is what clears leftovers from killed runs and
/// the historical backlog.
/// </para>
/// <para>
/// The two sweeps select differently on purpose, because the root is shared by every test run on the
/// machine - two worktrees can be running this suite at once. The start sweep keys on last-write time with
/// a margin no real run approaches, so it cannot touch a run that is still going. The end sweep keys on
/// creation during this run, which in principle includes a directory a concurrent run created after this
/// one started; in practice anything such a run is actively using has its SQLite file open and so cannot be
/// deleted, leaving only a sub-second write-then-read window on the JSON artifacts. That residual risk - a
/// spurious failure in a concurrent run, fixed by rerunning it - is accepted over leaking every run.
/// </para>
/// </remarks>
public sealed class TestTempDirectorySweeper : IDisposable
{
    /// <summary>The shared scratch root every test class in this assembly creates its directories under.</summary>
    internal static readonly string Root = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests");

    /// <summary>
    /// How long a directory must go unwritten before the start-of-run sweep treats it as abandoned. A day is
    /// far beyond any real run (the full suite takes under a minute), so this can never catch live work.
    /// </summary>
    internal static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(24);

    private readonly DateTime _runStartedUtc = DateTime.UtcNow;

    /// <summary>
    /// Runs the start-of-run sweep: deletes every directory under <see cref="Root"/> not written to for
    /// <see cref="AbandonedAfter"/>.
    /// </summary>
    public TestTempDirectorySweeper()
    {
        var cutoff = _runStartedUtc - AbandonedAfter;
        Sweep(directory => Directory.GetLastWriteTimeUtc(directory) < cutoff);
    }

    /// <summary>
    /// Runs the end-of-run sweep: releases every pooled SQLite handle, then deletes each directory under
    /// <see cref="Root"/> created during this run that per-test teardown failed to remove.
    /// </summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Sweep(directory => Directory.GetCreationTimeUtc(directory) >= _runStartedUtc);
    }

    /// <summary>
    /// Deletes each top-level directory under <see cref="Root"/> that <paramref name="shouldDelete"/> selects.
    /// Best-effort per directory: one that is locked or already gone is skipped, never allowed to fail the run.
    /// </summary>
    private static void Sweep(Func<string, bool> shouldDelete)
    {
        IEnumerable<string> directories;
        try
        {
            if (!Directory.Exists(Root)) return;
            directories = Directory.EnumerateDirectories(Root);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in directories)
            try
            {
                if (shouldDelete(directory)) Directory.Delete(path: directory, true);
            }
            catch (IOException)
            {
                // Locked by a live process, or deleted concurrently - either way not this sweep's to force.
            }
            catch (UnauthorizedAccessException)
            {
                // A read-only or ACL-restricted leftover; skipped rather than failing the run over scratch space.
            }
    }
}
