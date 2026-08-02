using System.Runtime.Versioning;
using TotallyHot.ArcRouter.Sandbox.Tier1;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>
/// Covers <see cref="CgroupManager"/>'s process-move and accounting-read methods, which take an arbitrary
/// leaf path and so are unit-testable against a plain temp directory - unlike <see cref="CgroupManager.Create"/>,
/// which is hardcoded to the real <c>/sys/fs/cgroup</c> tree and is exercised only by CI's Linux runner
/// (writing to that path from this Windows dev machine would either no-op against a nonexistent root or,
/// worse, actually create stray directories under the current drive - neither is a useful unit test).
/// </summary>
/// <remarks>
/// Marked <see cref="SupportedOSPlatformAttribute"/>("linux") purely to satisfy CA1416 for every call
/// site in this class, mirroring <c>FirecrackerMicroVmLauncherTests</c>. This attribute is compile-time
/// only - the methods under test have no actual Linux-only behavior (they just read/write plain files at
/// a caller-supplied path), so these tests run and assert for real on any OS, including this Windows dev
/// machine.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class CgroupManagerTests : IDisposable
{
    private readonly string _leafPath = Path.Combine(
        Path.GetTempPath(), "acrouter-cgroup-test-" + Guid.NewGuid().ToString("N"));

    private readonly CgroupManager _manager = new(NullLogger<CgroupManager>.Instance);

    public CgroupManagerTests()
    {
        Directory.CreateDirectory(_leafPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_leafPath))
            {
                Directory.Delete(_leafPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public void Constructor_RejectsNullLogger()
    {
        Assert.Throws<ArgumentNullException>(() => new CgroupManager(null!));
    }

    [Fact]
    public void AddProcess_WritesPidToCgroupProcs()
    {
        File.WriteAllText(Path.Combine(_leafPath, "cgroup.procs"), string.Empty);

        _manager.AddProcess(_leafPath, 4242);

        Assert.Equal("4242", File.ReadAllText(Path.Combine(_leafPath, "cgroup.procs")));
    }

    [Fact]
    public void AddProcess_NullOrEmptyLeafPath_NoOp()
    {
        _manager.AddProcess(string.Empty, 4242);
        _manager.AddProcess(null!, 4242);
    }

    [Fact]
    public void AddProcess_MissingLeafDirectory_SwallowsFailure()
    {
        var missing = Path.Combine(_leafPath, "does-not-exist");

        _manager.AddProcess(missing, 1);
    }

    [Fact]
    public void ReadOomKills_ParsesCountFromMemoryEvents()
    {
        File.WriteAllText(
            Path.Combine(_leafPath, "memory.events"),
            "low 0\nhigh 0\nmax 0\noom 1\noom_kill 3\n");

        Assert.Equal(3, _manager.ReadOomKills(_leafPath));
    }

    [Fact]
    public void ReadOomKills_MissingFile_ReturnsZero()
    {
        Assert.Equal(0, _manager.ReadOomKills(_leafPath));
    }

    [Fact]
    public void ReadOomKills_NullOrEmptyLeafPath_ReturnsZero()
    {
        Assert.Equal(0, _manager.ReadOomKills(null));
        Assert.Equal(0, _manager.ReadOomKills(string.Empty));
    }

    [Fact]
    public void ReadPeakMemory_ParsesValueFromMemoryPeak()
    {
        File.WriteAllText(Path.Combine(_leafPath, "memory.peak"), "134217728\n");

        Assert.Equal(134217728, _manager.ReadPeakMemory(_leafPath));
    }

    [Fact]
    public void ReadPeakMemory_MissingFile_ReturnsZero()
    {
        Assert.Equal(0, _manager.ReadPeakMemory(_leafPath));
    }

    [Fact]
    public void ReadPeakMemory_NullOrEmptyLeafPath_ReturnsZero()
    {
        Assert.Equal(0, _manager.ReadPeakMemory(null));
        Assert.Equal(0, _manager.ReadPeakMemory(string.Empty));
    }

    [Fact]
    public void Remove_DeletesLeafDirectory()
    {
        _manager.Remove(_leafPath);

        Assert.False(Directory.Exists(_leafPath));
    }

    [Fact]
    public void Remove_NullOrEmptyLeafPath_NoOp()
    {
        _manager.Remove(null);
        _manager.Remove(string.Empty);
    }

    [Fact]
    public void Remove_NonEmptyDirectory_SwallowsFailure()
    {
        File.WriteAllText(Path.Combine(_leafPath, "still-here.txt"), "x");

        // Directory.Delete (non-recursive) on cgroupfs maps to rmdir, which fails while the leaf still has
        // children - CgroupManager treats that as best-effort and must not throw.
        _manager.Remove(_leafPath);

        Assert.True(Directory.Exists(_leafPath));
    }
}

