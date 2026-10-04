using System.Runtime.InteropServices;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="AppDataPaths"/>'s contract and the test-run redirect (<see cref="TestAppDataDirectory"/>)
/// that keeps this suite out of the real data directory. Nothing here touches the real machine-wide
/// directory: the platform candidates are checked as pure path computations. The fallback chain, which
/// ADR-0024 moved into <see cref="TotallyHot.ArcRouter.Hosting.DataDirectory.DataDirectoryBootstrap"/>, is
/// covered by <c>DataDirectoryBootstrapTests</c> with temp candidates instead of the real
/// <c>%ProgramData%</c>. The Linux/macOS candidate
/// selection was verified for real on a Linux container during Phase P3 - see the web GUI migration
/// plan's P3 status section for that run's output, including a real bug (a last-resort/apphost filename
/// collision) it caught.
/// </summary>
public sealed class AppDataPathsTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void ResolveMachineSharedDirectory_UnderTest_IsThisRunsTempDirectory()
    {
        // Regression guard: before TestAppDataDirectory existed, a plain `dotnet test` run rewrote the real
        // %ProgramData%\TotallyHotArcRouter\web-interface.json (2026-10-02). If the module initializer ever
        // stops running first, this fails rather than the suite quietly writing there again.
        var directory = AppDataPaths.ResolveMachineSharedDirectory();

        Assert.Equal(expected: TestAppDataDirectory.MachineSharedDirectory, actual: directory);
        Assert.StartsWith(expectedStartString: Path.GetTempPath(), actualString: directory,
            comparisonType: StringComparison.OrdinalIgnoreCase);
        Assert.False(directory.StartsWith(value: AppDataPaths.MachineWideCandidate(),
            comparisonType: StringComparison.OrdinalIgnoreCase));
        Assert.False(directory.StartsWith(value: AppDataPaths.PerUserCandidate(),
            comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolvePerUserRoot_UnderTest_IsThisRunsTempDirectory()
    {
        // The other half of the guard: LegacyStorageMigration adopts files from under this root and renames
        // the originals aside, so under test it must never be the developer's real %LOCALAPPDATA%.
        Assert.Equal(expected: TestAppDataDirectory.PerUserRoot, actual: AppDataPaths.ResolvePerUserRoot());
        Assert.All(collection: StorageOptions.ResolveLegacyDirectories(),
            action: path => Assert.StartsWith(expectedStartString: TestAppDataDirectory.PerUserRoot,
                actualString: path, comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RedirectForTesting_AfterResolution_ThrowsAndKeepsTheResolvedDirectory()
    {
        var late = Path.Combine(path1: TestAppDataDirectory.Root, path2: "late-redirect");

        Assert.Throws<InvalidOperationException>(() =>
            AppDataPaths.RedirectForTesting(machineSharedDirectory: late, perUserRoot: late));

        Assert.Equal(expected: TestAppDataDirectory.MachineSharedDirectory,
            actual: AppDataPaths.ResolveMachineSharedDirectory());
        Assert.Equal(expected: TestAppDataDirectory.PerUserRoot, actual: AppDataPaths.ResolvePerUserRoot());
        Assert.False(Directory.Exists(late));
    }

    [Fact]
    public void ResolveMachineSharedDirectory_ReturnsAnExistingAbsoluteDirectory()
    {
        var directory = AppDataPaths.ResolveMachineSharedDirectory();

        Assert.True(Path.IsPathRooted(directory));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void ResolveMachineSharedDirectory_IsMemoizedAcrossCalls()
    {
        var first = AppDataPaths.ResolveMachineSharedDirectory();
        var second = AppDataPaths.ResolveMachineSharedDirectory();

        Assert.Same(expected: first, actual: second);
    }

    [Fact]
    public void MachineWideCandidate_OnWindows_IsTheApplicationDirectoryUnderProgramData()
    {
        // Only a Windows (or macOS) guarantee, not a universal one: Linux's own machine-wide default is
        // "/var/lib/totallyhot-arcrouter" (lowercase-hyphenated, not the literal "TotallyHotArcRouter"), and
        // an operator-supplied STATE_DIRECTORY can be any path at all (this repo's own Dockerfile sets it to
        // "/data" - see the web GUI migration plan's P10 status section for the real container crash that
        // exact case caused elsewhere in this codebase).
        if (!IsWindows) return;

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.Equal(expected: Path.Combine(path1: programData, path2: AppDataPaths.ApplicationDirectoryName),
            actual: AppDataPaths.MachineWideCandidate());
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
        // Off Windows the first directory is verified and, when missing, created owner-only (ADR-0024), so it
        // must be a scratch path rather than the real /var/log one, which a test account cannot create.
        var first = OperatingSystem.IsWindows()
            ? "/var/log/totallyhot-arcrouter"
            : Path.Combine(TestScratchDirectory.RunRoot, "logs-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("LOGS_DIRECTORY", first + ":/var/log/extra");
        try
        {
            var directory = AppDataPaths.ResolveLogsDirectory();

            Assert.Equal(expected: first, actual: directory);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(first));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGS_DIRECTORY", original);
        }
    }
}
