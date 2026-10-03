using Microsoft.Data.Sqlite;

[assembly: AssemblyFixture(typeof(TotallyHot.ArcRouter.Tests.TestTempDirectorySweeper))]

namespace TotallyHot.ArcRouter.Tests;

/// <summary>
/// Keeps <c>%TEMP%/arcrouter-tests</c> - the scratch root roughly forty test classes here create per-test
/// directories under, each run inside its own <see cref="TestScratchDirectory.RunRoot"/> - from growing
/// without bound. Registered as an xUnit assembly fixture, so it is built
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
/// So this sweeps twice. At the end of the run it clears every SQLite pool and deletes this run's own
/// <see cref="TestScratchDirectory.RunRoot"/>, which every test class here creates its directories inside: <see cref="SqliteConnection.ClearAllPools"/> is normally avoided in this suite because, mid-run,
/// it can tear a pooled native handle out from under a parallel test's in-flight query - but at assembly
/// teardown no test is running, so it is safe here and nowhere else. At the start of a run it deletes
/// anything untouched for <see cref="AbandonedAfter"/>, which is what clears leftovers from killed runs and
/// the historical backlog.
/// </para>
/// <para>
/// The two sweeps select differently on purpose, because the shared root is used by every test run on
/// the machine - two worktrees can be running this suite at once. The start sweep keys on last-write time
/// with a margin no real run approaches, so it cannot touch a run that is still going. The end sweep
/// deletes only the one directory this process owns by construction. An earlier version deleted every
/// top-level directory created since this run began, which could include a concurrent run's live
/// directories: on Unix an open SQLite file does not stop a directory delete, and JSON-only directories
/// were exposed on every platform (Copilot review on PR #186).
/// </para>
/// </remarks>
public sealed class TestTempDirectorySweeper : IDisposable
{
    /// <summary>The scratch root shared by every test run on this machine.</summary>
    private static readonly string Root = TestScratchDirectory.SharedRoot;

    /// <summary>
    /// How long a directory must go unwritten before the start-of-run sweep treats it as abandoned. A day is
    /// far beyond any real run (the full suite takes under a minute), so this can never catch live work.
    /// </summary>
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(24);

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
    /// Runs the end-of-run sweep: releases every pooled SQLite handle, then deletes this run's
    /// <see cref="TestScratchDirectory.RunRoot"/> and everything per-test teardown left in it. Nothing else
    /// under <see cref="Root"/> is touched, so a concurrent run's directories are safe.
    /// </summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Sweep(directory => string.Equals(a: directory, b: TestScratchDirectory.RunRoot,
            comparisonType: StringComparison.OrdinalIgnoreCase));
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
