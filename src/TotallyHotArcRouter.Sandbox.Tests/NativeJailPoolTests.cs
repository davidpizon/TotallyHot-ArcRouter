using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Tier1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the Tier-1 warm pool's lease/reset and concurrency bound (OS-agnostic paths).</summary>
public class NativeJailPoolTests
{
    private static NativeJailPool CreatePool(int size) =>
        new(Options.Create(new SandboxOptions { WarmPool = new WarmPoolOptions { Tier1 = size } }),
            NullLogger<NativeJailPool>.Instance);

    [Fact]
    public async Task LeaseAsync_CreatesWorkingDirectory_DisposeRemovesIt()
    {
        using var pool = CreatePool(2);

        var lease = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        var dir = lease.WorkingDirectory;

        Assert.True(Directory.Exists(dir));
        Assert.Equal(SandboxTier.Tier1Jail, lease.Tier);

        await lease.DisposeAsync();

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Tier_IsTier1Jail()
    {
        using var pool = CreatePool(1);

        Assert.Equal(SandboxTier.Tier1Jail, pool.Tier);
    }

    [Fact]
    public async Task DisposeAsync_DirectoryDeletionFails_SwallowsAndStillReleasesSlot()
    {
        using var pool = CreatePool(1);

        var lease = await pool.LeaseAsync(TestContext.Current.CancellationToken);

        // The pool must swallow a failed recursive delete (log-and-continue) rather than throw, and must
        // still release the semaphore slot so the pool isn't wedged for later leases. Forcing that failure
        // needs a platform-specific technique: POSIX permits unlinking a file that's still open (the file
        // just stays around until the last handle closes), so an exclusive FileStream lock - which works
        // on Windows - doesn't block deletion on Linux/macOS. There, deleting an entry requires
        // write+execute on its *parent* directory instead, so stripping those bits from the working
        // directory blocks the recursive delete from removing the file inside it.
        if (OperatingSystem.IsWindows())
        {
            var lockedFile = Path.Combine(lease.WorkingDirectory, "locked.txt");
            await using var handle = new FileStream(lockedFile, FileMode.Create, FileAccess.Write, FileShare.None);

            await lease.DisposeAsync();

            Assert.True(Directory.Exists(lease.WorkingDirectory), "Directory should survive a failed delete.");
        }
        else
        {
            var blockedFile = Path.Combine(lease.WorkingDirectory, "blocked.txt");
            await File.WriteAllTextAsync(blockedFile, "x", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(lease.WorkingDirectory, UnixFileMode.UserRead);
            try
            {
                await lease.DisposeAsync();

                Assert.True(Directory.Exists(lease.WorkingDirectory), "Directory should survive a failed delete.");
            }
            finally
            {
                File.SetUnixFileMode(lease.WorkingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // The slot was released despite the failed delete: a second lease must not block.
        var second = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task LeaseAsync_BoundsConcurrency()
    {
        using var pool = CreatePool(1);

        var first = await pool.LeaseAsync(TestContext.Current.CancellationToken);

        var secondLease = pool.LeaseAsync(TestContext.Current.CancellationToken);
        var completedEarly = secondLease.IsCompleted;

        await first.DisposeAsync();
        var second = await secondLease;

        Assert.False(completedEarly);
        Assert.True(Directory.Exists(second.WorkingDirectory));

        await second.DisposeAsync();
    }
}

