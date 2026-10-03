using System.Runtime.InteropServices;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="AppDataPaths"/>'s contract and the test-run redirect (<see cref="TestAppDataDirectory"/>)
/// that keeps this suite out of the real data directory. Nothing here touches the real machine-wide
/// directory: the platform candidates are checked as pure path computations, and the fallback chain is
/// driven through <see cref="AppDataPaths.SelectUsableDirectory"/> with temp candidates instead of
/// write-probing <c>%ProgramData%</c> the way production resolution does. The Linux/macOS candidate
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
    public void SelectUsableDirectory_WritableMachineWide_IsChosenAndLeftWithoutAProbeFile()
    {
        var scratch = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var machineWide = Path.Combine(path1: scratch, path2: "machine");
            var perUser = Path.Combine(path1: scratch, path2: "user");

            var chosen = AppDataPaths.SelectUsableDirectory(machineWide: machineWide, perUser: perUser,
                lastResort: Path.Combine(path1: scratch, path2: "last"));

            Assert.Equal(expected: machineWide, actual: chosen);
            Assert.Empty(Directory.EnumerateFileSystemEntries(machineWide));
            Assert.False(Directory.Exists(perUser));
        }
        finally
        {
            Directory.Delete(path: scratch, recursive: true);
        }
    }

    [Fact]
    public void SelectUsableDirectory_UnusableMachineWide_FallsBackToPerUser()
    {
        var scratch = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // A directory can't be created beneath a file on any platform - a deterministic stand-in for a
            // machine-wide directory this account may not write to.
            var blocker = Path.Combine(path1: scratch, path2: "blocker");
            File.WriteAllText(path: blocker, contents: string.Empty);
            var perUser = Path.Combine(path1: scratch, path2: "user");

            var chosen = AppDataPaths.SelectUsableDirectory(
                machineWide: Path.Combine(path1: blocker, path2: "machine"), perUser: perUser,
                lastResort: Path.Combine(path1: scratch, path2: "last"));

            Assert.Equal(expected: perUser, actual: chosen);
        }
        finally
        {
            Directory.Delete(path: scratch, recursive: true);
        }
    }

    [Fact]
    public void SelectUsableDirectory_NeitherCandidateUsable_CreatesTheLastResort()
    {
        var scratch = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var blocker = Path.Combine(path1: scratch, path2: "blocker");
            File.WriteAllText(path: blocker, contents: string.Empty);
            var lastResort = Path.Combine(path1: scratch, path2: "last");

            var chosen = AppDataPaths.SelectUsableDirectory(
                machineWide: Path.Combine(path1: blocker, path2: "machine"),
                perUser: Path.Combine(path1: blocker, path2: "user"), lastResort: lastResort);

            Assert.Equal(expected: lastResort, actual: chosen);
            Assert.True(Directory.Exists(lastResort));
        }
        finally
        {
            Directory.Delete(path: scratch, recursive: true);
        }
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
