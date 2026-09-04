using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="WebViewUserData"/>: the per-user folder it resolves, its deference to an
/// explicit override, and the process-environment side effect <c>MauiProgram</c> depends on. Guards the
/// fix for the installed GUI's blank dashboard, where WebView2's default folder landed under
/// %ProgramFiles% and could not be created.
/// </summary>
public sealed class WebViewUserDataTests
{
    [Fact]
    public void ResolveFolder_WithNoOverride_UsesPerUserApplicationData()
    {
        var folder = WebViewUserData.ResolveFolder(@"C:\Users\example\AppData\Local");

        folder.Should().Be(@"C:\Users\example\AppData\Local\TotallyHotArcRouter\WebView2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveFolder_WithBlankOverride_FallsBackToApplicationData(string? blank)
    {
        var folder = WebViewUserData.ResolveFolder(localApplicationDataPath: @"C:\Users\example\AppData\Local",
            existingOverride: blank);

        folder.Should().Be(@"C:\Users\example\AppData\Local\TotallyHotArcRouter\WebView2");
    }

    [Fact]
    public void ResolveFolder_WithOverride_KeepsIt()
    {
        var folder = WebViewUserData.ResolveFolder(localApplicationDataPath: @"C:\Users\example\AppData\Local",
            existingOverride: @"D:\webview-cache");

        folder.Should().Be(@"D:\webview-cache");
    }

    [Fact]
    public void ResolveFolder_NeverLandsBesideTheExecutable()
    {
        // The whole point of the redirect: the resolved folder must not be under the install directory,
        // which for the MSI-installed build is %ProgramFiles%\TotallyHotArcRouter\Gui and read-only.
        var folder = WebViewUserData.ResolveFolder(@"C:\Users\example\AppData\Local");

        folder.Should().NotStartWith(AppContext.BaseDirectory);
    }

    [Fact]
    public void Apply_PublishesAnExistingFolderToTheEnvironment()
    {
        var original = Environment.GetEnvironmentVariable(WebViewUserData.UserDataFolderVariable);
        try
        {
            Environment.SetEnvironmentVariable(variable: WebViewUserData.UserDataFolderVariable, null);

            var folder = WebViewUserData.Apply();

            Environment.GetEnvironmentVariable(WebViewUserData.UserDataFolderVariable).Should().Be(folder);
            Directory.Exists(folder).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: WebViewUserData.UserDataFolderVariable, value: original);
        }
    }

    [Fact]
    public void Apply_LeavesAnExplicitOverrideInPlace()
    {
        var original = Environment.GetEnvironmentVariable(WebViewUserData.UserDataFolderVariable);
        var chosen = Path.Combine(path1: Path.GetTempPath(), path2: Guid.NewGuid().ToString());
        try
        {
            Environment.SetEnvironmentVariable(variable: WebViewUserData.UserDataFolderVariable, value: chosen);

            WebViewUserData.Apply().Should().Be(chosen);

            Environment.GetEnvironmentVariable(WebViewUserData.UserDataFolderVariable).Should().Be(chosen);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: WebViewUserData.UserDataFolderVariable, value: original);
            Directory.Delete(path: chosen, true);
        }
    }
}