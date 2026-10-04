using System.Runtime.Versioning;
using TotallyHot.ArcRouter.Hosting.DataDirectory;
using static TotallyHot.ArcRouter.Tests.Hosting.DataDirectory.WindowsDataDirectoryTestSupport;

namespace TotallyHot.ArcRouter.Tests.Hosting.DataDirectory;

/// <summary>
/// Covers <see cref="DataDirectoryBootstrap.DecideWindows"/>, the startup decision ADR-0024 rule 2 adds in
/// front of the old machine-wide/per-user/last-resort chain: use the machine-wide directory only when it
/// is protected, create it protected when an elevated process finds none, fail closed when the service
/// finds an unprotected one, and otherwise fall back. The decision is driven against temp directories
/// with elevation passed in, so nothing here touches <c>%ProgramData%</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DataDirectoryBootstrapTests
{
    [Fact]
    public void ProtectedMachineWide_IsUsed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var machineWide = Path.Combine(scratch, "machine");
            WindowsDataDirectorySecurity.CreateProtected(machineWide, TestPolicy);

            var resolution = Decide(scratch, machineWide, elevated: false);

            Assert.Equal(machineWide, resolution.Directory);
            Assert.True(resolution.UsingProtectedMachineWide);
            Assert.False(resolution.MachineWideUnavailable);
            Assert.Empty(Directory.EnumerateFileSystemEntries(machineWide));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void MissingMachineWide_Elevated_IsCreatedProtected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var machineWide = Path.Combine(scratch, "machine");

            var resolution = Decide(scratch, machineWide, elevated: true);

            Assert.Equal(machineWide, resolution.Directory);
            Assert.Equal(DataDirectoryState.Protected, WindowsDataDirectorySecurity.Inspect(machineWide, TestPolicy).State);
            Assert.Single(resolution.Events);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void MissingMachineWide_Unelevated_FallsBackWithoutCreatingIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var machineWide = Path.Combine(scratch, "machine");

            var resolution = Decide(scratch, machineWide, elevated: false);

            Assert.Equal(Path.Combine(scratch, "user"), resolution.Directory);
            Assert.False(Directory.Exists(machineWide));
            // No service data exists, so a per-user run is not "missing" anything.
            Assert.False(resolution.MachineWideUnavailable);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void UnprotectedMachineWide_Elevated_FailsClosed_AndLeavesItAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var machineWide = Directory.CreateDirectory(Path.Combine(scratch, "machine")).FullName;
            File.WriteAllText(Path.Combine(machineWide, "transcripts.db"), "data");

            var ex = Assert.Throws<DataDirectoryNotProtectedException>(() =>
                Decide(scratch, machineWide, elevated: true));

            Assert.Equal(machineWide, ex.Path);
            Assert.Contains("--migrate-data-directory", ex.Message);
            Assert.True(File.Exists(Path.Combine(machineWide, "transcripts.db")));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void UnprotectedMachineWide_Unelevated_FallsBack_AndReportsItUnavailable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var machineWide = Directory.CreateDirectory(Path.Combine(scratch, "machine")).FullName;

            var resolution = Decide(scratch, machineWide, elevated: false);

            Assert.Equal(Path.Combine(scratch, "user"), resolution.Directory);
            Assert.False(resolution.UsingProtectedMachineWide);
            Assert.True(resolution.MachineWideUnavailable);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void MissingMachineWide_WithAnInterruptedMigration_Elevated_FailsClosed_InsteadOfCreatingAnEmptyRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            var machineWide = Path.Combine(scratch, "machine");
            WindowsDataDirectorySecurity.CreateProtected(DataDirectoryMigrationRules.NewStagingPath(machineWide),
                TestPolicy);

            Assert.Throws<DataDirectoryNotProtectedException>(() => Decide(scratch, machineWide, elevated: true));
            Assert.False(Directory.Exists(machineWide));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public void NeitherCandidateUsable_CreatesTheLastResort()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs");
        var scratch = NewScratch();
        try
        {
            // A directory can't be created beneath a file - a deterministic stand-in for candidates this
            // account may not use.
            var blocker = Path.Combine(scratch, "blocker");
            File.WriteAllText(blocker, string.Empty);
            var lastResort = Path.Combine(scratch, "last");

            var resolution = DataDirectoryBootstrap.DecideWindows(machineWide: Path.Combine(blocker, "machine"),
                perUser: Path.Combine(blocker, "user"), lastResort: lastResort, policy: TestPolicy, elevated: false);

            Assert.Equal(lastResort, resolution.Directory);
            Assert.True(Directory.Exists(lastResort));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    private static DataDirectoryResolution Decide(string scratch, string machineWide, bool elevated)
    {
        return DataDirectoryBootstrap.DecideWindows(machineWide: machineWide, perUser: Path.Combine(scratch, "user"),
            lastResort: Path.Combine(scratch, "last"), policy: TestPolicy, elevated: elevated);
    }
}
