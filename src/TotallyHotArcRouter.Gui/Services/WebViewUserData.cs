namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Points WebView2 at a writable per-user data folder before the first <c>BlazorWebView</c> is created.
/// </summary>
/// <remarks>
/// This exists because of where the MSI installs the GUI. WebView2 needs a read/write user-data folder
/// (its cache, profile, and the browser process' own scratch space), and for an unpackaged app - which
/// this is, see the csproj's <c>WindowsPackageType=None</c> - the loader defaults that folder to
/// <c>&lt;exe&gt;.WebView2</c> beside the executable. In a development build that is <c>bin\Debug\...</c>
/// and works; once installed it is <c>%ProgramFiles%\TotallyHotArcRouter\Gui\</c>, which the interactive
/// user cannot write to. Creating the environment then fails, the control never gets a CoreWebView2, and
/// the dashboard window opens completely blank with no crash and nothing logged - which is exactly the
/// symptom the installed build showed. Redirecting the folder to <c>%LOCALAPPDATA%</c> (the same
/// per-user root <see cref="GuiSettingsStore"/> already uses) is Microsoft's documented remedy for
/// apps installed under Program Files.
/// </remarks>
public static class WebViewUserData
{
    /// <summary>
    /// The environment variable the WebView2 loader reads when the host passes no explicit user-data
    /// folder. MAUI's BlazorWebView handler does not pass one, so this variable is the only seam
    /// available for redirecting it without replacing the handler.
    /// </summary>
    public const string UserDataFolderVariable = "WEBVIEW2_USER_DATA_FOLDER";

    /// <summary>
    /// Resolves the folder WebView2 should use: <paramref name="existingOverride"/> when the environment
    /// already names one (an operator or a test setting it explicitly wins), otherwise
    /// <c>%LOCALAPPDATA%\TotallyHotArcRouter\WebView2</c>.
    /// </summary>
    /// <param name="localApplicationDataPath">The per-user application-data root to place the folder under.</param>
    /// <param name="existingOverride">The value already present in the environment, if any.</param>
    /// <returns>The absolute path WebView2 should be pointed at.</returns>
    public static string ResolveFolder(string localApplicationDataPath, string? existingOverride = null)
    {
        return string.IsNullOrWhiteSpace(existingOverride)
            ? Path.Combine(path1: localApplicationDataPath, path2: "TotallyHotArcRouter", path3: "WebView2")
            : existingOverride;
    }

    /// <summary>
    /// Creates the resolved folder and publishes it to the current process' environment. Must run before
    /// the first BlazorWebView is constructed - the loader reads the variable once, when it creates the
    /// WebView2 environment - which is why <c>MauiProgram.CreateMauiApp</c> calls this as its first
    /// statement.
    /// </summary>
    /// <returns>The folder now in effect, for logging or diagnostics by the caller.</returns>
    public static string Apply()
    {
        var folder = ResolveFolder(
            localApplicationDataPath: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            existingOverride: Environment.GetEnvironmentVariable(UserDataFolderVariable));

        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable(variable: UserDataFolderVariable, value: folder);
        return folder;
    }
}