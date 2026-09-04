namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// Reads the per-user management token the proxy generates (see
/// <c>TotallyHot.ArcRouter.Proxy.Management.ManagementAccessToken</c>) so the GUI can present it on every
/// <c>/admin/*</c> request now that the API requires it by default. This project deliberately doesn't
/// reference the proxy executable (see <see cref="ProviderAdminClient"/>'s remarks), so the file path and
/// read logic are duplicated here - the same "one fact, two copies because of the process boundary"
/// tradeoff as <c>TotallyHot.ArcRouter.Gui.Telemetry.TelemetryChannelFactory</c>'s certificate trust callback.
/// </summary>
public static class ManagementTokenReader
{
    private const string TokenFileName = "management-token.txt";

    /// <summary>
    /// Reads the token from <paramref name="path"/> (or, when <see langword="null"/>, the default
    /// <c>%ProgramData%\TotallyHotArcRouter\management-token.txt</c>), or <see langword="null"/> when the file
    /// doesn't exist yet (e.g. the proxy has never started), is empty/whitespace, or can't be read. A
    /// caller that gets <see langword="null"/> here will see every <c>/admin/*</c> request come back 401
    /// until the proxy has run at least once to generate the file.
    /// </summary>
    /// <param name="path">The token file path override; only meant for tests. Production callers omit it.</param>
    public static string? TryRead(string? path = null)
    {
        try
        {
            var tokenPath = path ?? DefaultPath();

            if (!File.Exists(tokenPath)) return null;

            var token = File.ReadAllText(tokenPath).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the default token file path: <c>%ProgramData%\TotallyHotArcRouter\management-token.txt</c>.
    /// Machine-wide, not per-user: the installed router runs as <c>LocalSystem</c> while this GUI runs as the
    /// interactive user, so the per-user <c>%LOCALAPPDATA%</c> this used to read resolved to a different file
    /// than the one the router wrote. Must stay in step with <c>ManagementAccessToken.DefaultPath</c>.
    /// </summary>
    private static string DefaultPath()
    {
        return Path.Combine(
            path1: Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            path2: "TotallyHotArcRouter",
            path3: TokenFileName);
    }
}