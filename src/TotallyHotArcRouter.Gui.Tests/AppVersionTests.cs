using System.Reflection;
using TotallyHot.ArcRouter.Gui.Services;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="AppVersion"/>: the build-metadata stripping the System Settings footer depends
/// on, and the fallback when an assembly carries no informational version at all.
/// </summary>
public sealed class AppVersionTests
{
    [Fact]
    public void Strip_RemovesTheGitShaTheSdkAppends()
    {
        AppVersion.Strip("1.0.2+8c8226dabc").Should().Be("1.0.2");
    }

    [Fact]
    public void Strip_LeavesAPlainVersionAlone()
    {
        AppVersion.Strip("1.0.2").Should().Be("1.0.2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Strip_WithNothingToRead_ReportsUnknown(string? blank)
    {
        AppVersion.Strip(blank).Should().Be(AppVersion.Unknown);
    }

    [Fact]
    public void Read_UsesTheAssemblysInformationalVersion()
    {
        var assembly = typeof(AppVersion).Assembly;
        var expected = AppVersion.Strip(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        AppVersion.Read(assembly).Should().Be(expected);
    }

    [Fact]
    public void Current_IsAPlainVersionWithNoBuildMetadata()
    {
        // What the footer renders after "GUI v" - a git-sha suffix leaking through would make the label
        // wrap and stop being comparable to the Router's half at a glance.
        AppVersion.Current.Should().NotBeNullOrWhiteSpace();
        AppVersion.Current.Should().NotContain("+");
    }
}
