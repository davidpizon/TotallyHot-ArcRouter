using TotallyHot.ArcRouter.Sandbox.Firecracker;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers Firecracker binary location.</summary>
public class FirecrackerLocatorTests
{
    [Fact]
    public void Find_ConfiguredPathThatExists_IsReturned()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fc-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(temp, "#!/bin/sh\n");
        try
        {
            Assert.Equal(temp, FirecrackerLocator.Find(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Find_ConfiguredPathMissing_FallsThroughOrReturnsNull()
    {
        // A path that does not exist must not be returned as-is.
        var result = FirecrackerLocator.Find("/no/such/firecracker/binary/here");

        Assert.NotEqual("/no/such/firecracker/binary/here", result);
    }

    [Fact]
    public void Find_NoConfiguredPath_FallsBackToPathEnvironmentVariable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fc-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var binary = Path.Combine(directory, "firecracker");
        File.WriteAllText(binary, "#!/bin/sh\n");

        var originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);
        try
        {
            Assert.Equal(binary, FirecrackerLocator.Find(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Find_NullConfiguredPath_AndEmptyPathEnv_ReturnsNull()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Empty);
        try
        {
            Assert.Null(FirecrackerLocator.Find(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void IsAvailable_FalseOffLinux()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.False(FirecrackerLocator.IsAvailable(null));
        Assert.False(FirecrackerLocator.IsAvailable("/anything"));
    }
}

