using TotallyHot.ArcRouter.Sandbox.Firecracker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the Tier-2 warm pool's lease/reset and concurrency bound (OS-agnostic paths).</summary>
public class FirecrackerSnapshotPoolTests
{
    private static FirecrackerSnapshotPool CreatePool(int size) =>
        new(Options.Create(new SandboxOptions { WarmPool = new WarmPoolOptions { Tier2 = size } }),
            NullLogger<FirecrackerSnapshotPool>.Instance);

    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new FirecrackerSnapshotPool(null!, NullLogger<FirecrackerSnapshotPool>.Instance));
        Assert.Throws<ArgumentNullException>(() => new FirecrackerSnapshotPool(Options.Create(new SandboxOptions()), null!));
    }

    [Fact]
    public void Tier_IsTier2MicroVm()
    {
        using var pool = CreatePool(1);

        Assert.Equal(SandboxTier.Tier2MicroVm, pool.Tier);
    }

    [Fact]
    public async Task LeaseAsync_CreatesWorkingDirectory_DisposeRemovesIt()
    {
        using var pool = CreatePool(2);

        var lease = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        var dir = lease.WorkingDirectory;

        Assert.True(Directory.Exists(dir));
        Assert.Equal(SandboxTier.Tier2MicroVm, lease.Tier);

        await lease.DisposeAsync();

        Assert.False(Directory.Exists(dir));
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

    [Fact]
    public async Task DisposeAsync_DirectoryDeletionFails_SwallowsAndStillReleasesSlot()
    {
        using var pool = CreatePool(1);

        var lease = await pool.LeaseAsync(TestContext.Current.CancellationToken);

        // See the matching comment in NativeJailPoolTests: POSIX permits unlinking an open file, so an
        // exclusive FileStream lock (the Windows technique below) doesn't block deletion on Linux/macOS -
        // there, stripping write+execute from the working directory itself blocks the recursive delete
        // from removing the file inside it.
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

        var second = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        await second.DisposeAsync();
    }

    [Fact]
    public void Constructor_UsesConfiguredRuntimeDirectory()
    {
        var runtimeDir = Path.Combine(Path.GetTempPath(), "acrouter-fc-runtime-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var pool = new FirecrackerSnapshotPool(
                Options.Create(new SandboxOptions { Firecracker = new FirecrackerOptions { RuntimeDirectory = runtimeDir } }),
                NullLogger<FirecrackerSnapshotPool>.Instance);

            Assert.True(Directory.Exists(runtimeDir));
        }
        finally
        {
            if (Directory.Exists(runtimeDir))
            {
                Directory.Delete(runtimeDir, recursive: true);
            }
        }
    }
}

