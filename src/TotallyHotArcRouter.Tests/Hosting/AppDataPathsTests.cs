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
    }

    [Fact]
    public void ResolveMachineSharedDirectory_OnWindows_EndsWithApplicationDirectoryName()
    {
        // Only a Windows (or macOS/per-user-fallback) guarantee, not a universal one: Linux's own
        // machine-wide default is "/var/lib/totallyhot-arcrouter" (lowercase-hyphenated, not the literal
        // "TotallyHotArcRouter"), and an operator-supplied STATE_DIRECTORY can be any path at all (this
        // repo's own Dockerfile sets it to "/data" - see the web GUI migration plan's P10 status section
        // for the real container crash that exact case caused elsewhere in this codebase). Asserting this
        // unconditionally used to pass here only because this repo's own CI runner is unprivileged and so
        // always fell through to the per-user fallback, which happens to end in the exact name - a
        // coincidence of this test's own execution environment, not a contract AppDataPaths documents for
        // every platform.
        if (!IsWindows) return;

        var directory = AppDataPaths.ResolveMachineSharedDirectory();

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

    [Fact]
    public void ResolveLogsDirectory_WithNoLogsDirectoryVariable_IsALogsSubdirectoryOfMachineShared()
    {
        var original = Environment.GetEnvironmentVariable("LOGS_DIRECTORY");
        Environment.SetEnvironmentVariable("LOGS_DIRECTORY", null);
        try
        {
            var directory = AppDataPaths.ResolveLogsDirectory();

            Assert.Equal(expected: Path.Combine(AppDataPaths.ResolveMachineSharedDirectory(), "logs"),
                actual: directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGS_DIRECTORY", original);
        }
    }

    [Fact]
    public void ResolveLogsDirectory_WithLogsDirectoryVariable_UsesItVerbatim()
    {
        var original = Environment.GetEnvironmentVariable("LOGS_DIRECTORY");
        // systemd's LogsDirectory= can be colon-separated when a unit names more than one directory -
        // only the first is used, matching ResolveMachineSharedDirectory's own STATE_DIRECTORY handling.
        Environment.SetEnvironmentVariable("LOGS_DIRECTORY", "/var/log/totallyhot-arcrouter:/var/log/extra");
        try
        {
            var directory = AppDataPaths.ResolveLogsDirectory();

            Assert.Equal(expected: "/var/log/totallyhot-arcrouter", actual: directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGS_DIRECTORY", original);
        }
    }
}
