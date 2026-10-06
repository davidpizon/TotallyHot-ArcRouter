using TotallyHot.ArcRouter.Hosting.DataDirectory;

namespace TotallyHot.ArcRouter.Tests.Hosting.DataDirectory;

/// <summary>
/// Covers <see cref="DataDirectoryAdvice"/>: the migration command is built for the way the process was
/// launched so it can be pasted as written, and the explanation ends in steps rather than a stack trace.
/// Paths use forward slashes, which both Windows and Unix accept, so the cases do not depend on the host.
/// </summary>
public sealed class DataDirectoryAdviceTests
{
    [Fact]
    public void MigrationCommand_InstalledExecutableWithSpaces_IsQuotedWithCallOperatorOnWindows()
    {
        var command = DataDirectoryAdvice.MigrationCommand(
            processPath: "C:/Program Files/TotallyHot/TotallyHotArcRouter.exe", entryAssemblyPath: null, windows: true);

        Assert.Equal("& \"C:/Program Files/TotallyHot/TotallyHotArcRouter.exe\" --migrate-data-directory", command);
    }

    [Fact]
    public void MigrationCommand_ExecutableWithoutSpaces_IsLeftUnquoted()
    {
        var command = DataDirectoryAdvice.MigrationCommand(
            processPath: "C:/tools/TotallyHotArcRouter.exe", entryAssemblyPath: null, windows: true);

        Assert.Equal("C:/tools/TotallyHotArcRouter.exe --migrate-data-directory", command);
    }

    [Fact]
    public void MigrationCommand_DotnetHost_RunsTheEntryAssembly()
    {
        var command = DataDirectoryAdvice.MigrationCommand(
            processPath: "C:/Program Files/dotnet/dotnet.exe",
            entryAssemblyPath: "C:/git/Arc Router/bin/TotallyHotArcRouter.dll", windows: true);

        Assert.Equal("dotnet \"C:/git/Arc Router/bin/TotallyHotArcRouter.dll\" --migrate-data-directory", command);
    }

    [Fact]
    public void MigrationCommand_DotnetHostWithoutEntryAssembly_FallsBackToTheHostPath()
    {
        var command = DataDirectoryAdvice.MigrationCommand(
            processPath: "C:/tools/dotnet.exe", entryAssemblyPath: null, windows: true);

        Assert.Equal("C:/tools/dotnet.exe --migrate-data-directory", command);
    }

    [Fact]
    public void MigrationCommand_UnknownProcessPath_UsesTheProductName()
    {
        var command = DataDirectoryAdvice.MigrationCommand(processPath: null, entryAssemblyPath: null, windows: true);

        Assert.Equal("TotallyHotArcRouter --migrate-data-directory", command);
    }

    [Fact]
    public void MigrationCommand_OnUnix_RunsAsRootWithoutPowerShellSyntax()
    {
        var command = DataDirectoryAdvice.MigrationCommand(
            processPath: "/opt/totallyhot arcrouter/TotallyHotArcRouter", entryAssemblyPath: null, windows: false);

        Assert.Equal("sudo \"/opt/totallyhot arcrouter/TotallyHotArcRouter\" --migrate-data-directory", command);
    }

    [Fact]
    public void Guidance_OnWindows_NamesTheElevationStepTheCommandAndTheSettingsCaveat()
    {
        var text = DataDirectoryAdvice.Guidance(migrationCommand: "app --migrate-data-directory", windows: true);

        Assert.Contains("elevated PowerShell", text, StringComparison.Ordinal);
        Assert.Contains("Run: app --migrate-data-directory", text, StringComparison.Ordinal);
        Assert.Contains("Start the router again", text, StringComparison.Ordinal);
        Assert.Contains("appsettings.local.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Guidance_OffWindows_AsksForRootInsteadOfPowerShell()
    {
        var text = DataDirectoryAdvice.Guidance(migrationCommand: "sudo app --migrate-data-directory", windows: false);

        Assert.Contains("as root", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerShell", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Guidance_ForThisProcess_NamesTheMigrationFlag()
    {
        var text = DataDirectoryAdvice.Guidance();

        Assert.Contains(DataDirectoryMigrationCommand.FlagName, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_KeepsReasonAndStillNamesTheRemedyInItsMessage()
    {
        var ex = new DataDirectoryNotProtectedException(path: "C:/ProgramData/TotallyHotArcRouter",
            reason: "it is owned by THEATRE-PC/david, which can rewrite its permissions");

        Assert.Equal("it is owned by THEATRE-PC/david, which can rewrite its permissions", ex.Reason);
        Assert.StartsWith(
            DataDirectoryNotProtectedException.Describe(path: ex.Path, reason: ex.Reason), ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("--migrate-data-directory", ex.Message, StringComparison.Ordinal);
    }
}
