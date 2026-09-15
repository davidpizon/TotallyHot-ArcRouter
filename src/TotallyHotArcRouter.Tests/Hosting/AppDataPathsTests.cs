using System.Runtime.InteropServices;
using TotallyHot.ArcRouter.Hosting;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="AppDataPaths"/>'s public contract. The result is memoized for the process's
/// lifetime, so this can only exercise the one outcome this test process's actual environment produces
/// (the machine-wide Windows candidate, on the CI/dev machines this repo's test suite runs on) - the
/// Linux/macOS candidate selection, per-user fallback, and last-resort collision-avoidance logic (see
/// <see cref="AppDataPaths"/>'s remarks) were verified for real on a Linux container during Phase P3's
/// implementation instead; see the web GUI migration plan's P3 status section for that run's output,
/// including a real bug (a last-resort/apphost filename collision) this exact process caught.
/// </summary>
public sealed class AppDataPathsTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void ResolveMachineSharedDirectory_ReturnsAnExistingAbsoluteDirectory()
    {
        var directory = AppDataPaths.ResolveMachineSharedDirectory();

        Assert.True(Path.IsPathRooted(directory));
        Assert.True(Directory.Exists(directory));
        Assert.EndsWith(expectedEndString: AppDataPaths.ApplicationDirectoryName, actualString: directory,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveMachineSharedDirectory_IsMemoizedAcrossCalls()
    {
        var first = AppDataPaths.ResolveMachineSharedDirectory();
        var second = AppDataPaths.ResolveMachineSharedDirectory();

        Assert.Same(expected: first, actual: second);
    }

    [Fact]
    public void ResolveMachineSharedDirectory_OnWindows_IsUnderProgramData()
    {
        if (!IsWindows) return;

        var directory = AppDataPaths.ResolveMachineSharedDirectory();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.StartsWith(expectedStartString: programData, actualString: directory,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}
