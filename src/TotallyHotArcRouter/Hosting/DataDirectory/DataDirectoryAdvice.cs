using System.Reflection;

namespace TotallyHot.ArcRouter.Hosting.DataDirectory;

/// <summary>
/// Builds the operator-facing explanation logged when the router refuses to start on an unprotected data
/// directory (ADR-0024), so the failure ends in a short, actionable fatal message instead of the exception's
/// stack trace.
/// </summary>
/// <remarks>
/// The exception's own text names <c>--migrate-data-directory</c>, but as a bare product name that is not on
/// the operator's <c>PATH</c>, and it cannot say how to get an elevated prompt or what the command will do to
/// existing settings. This type adds those: the exact command line for however this process was launched
/// (installed executable, or <c>dotnet</c> running a build output), the elevation step for the platform, and
/// the one consequence an operator must know before running it - <c>appsettings.local.json</c> is not carried
/// over.
/// </remarks>
internal static class DataDirectoryAdvice
{
    private const string ProductName = "TotallyHotArcRouter";

    /// <summary>
    /// Formats the steps for fixing a refused data directory, using this process's own path and platform.
    /// Passed to the logger as a structured property so the log message template stays a static literal.
    /// </summary>
    /// <returns>A multi-line list of steps, without the headline that states what failed.</returns>
    public static string Guidance() => Guidance(
        migrationCommand: MigrationCommand(processPath: Environment.ProcessPath,
            entryAssemblyPath: Assembly.GetEntryAssembly()?.Location, windows: OperatingSystem.IsWindows()),
        windows: OperatingSystem.IsWindows());

    /// <summary>
    /// Formats the steps for a given command line and platform, separated from <see cref="Guidance()"/> so
    /// tests do not depend on the machine they run on.
    /// </summary>
    /// <param name="migrationCommand">The command line to run, from <see cref="MigrationCommand"/>.</param>
    /// <param name="windows">Whether to give Windows elevation instructions rather than root ones.</param>
    /// <returns>A multi-line list of steps, without the headline that states what failed.</returns>
    internal static string Guidance(string migrationCommand, bool windows)
    {
        var elevate = windows
            ? "Open an elevated PowerShell (right-click PowerShell, then Run as administrator)."
            : "Open a terminal; the command has to run as root.";

        return string.Join(separator: Environment.NewLine, values:
        [
            "To fix this, migrate the directory once:",
            $"  1. {elevate}",
            $"  2. Run: {migrationCommand}",
            "  3. Start the router again.",
            "Existing data is migrated into a protected directory, and the command is safe to run again. It does not carry over appsettings.local.json, because any local account could have planted one; re-apply the settings you need from an elevated prompt (ADR-0024)."
        ]);
    }

    /// <summary>
    /// Builds the command line that runs <see cref="DataDirectoryMigrationCommand.FlagName"/> the way this
    /// process was started.
    /// </summary>
    /// <param name="processPath">This process's executable (<see cref="Environment.ProcessPath"/>), or <see langword="null"/> when unknown.</param>
    /// <param name="entryAssemblyPath">The entry assembly's path, used only when <paramref name="processPath"/> is the <c>dotnet</c> host.</param>
    /// <param name="windows">Whether to prefix a quoted path with PowerShell's call operator, or <c>sudo</c> otherwise.</param>
    /// <returns>
    /// A command line that works as written: <c>dotnet "app.dll" ...</c> when launched through the
    /// <c>dotnet</c> host (a source checkout run with <c>dotnet run</c>), otherwise the executable's own path,
    /// or the bare product name when the path is unknown.
    /// </returns>
    internal static string MigrationCommand(string? processPath, string? entryAssemblyPath, bool windows)
    {
        var flag = DataDirectoryMigrationCommand.FlagName;
        var launcher = string.IsNullOrWhiteSpace(processPath) ? ProductName : processPath;

        string command;
        if (string.Equals(Path.GetFileNameWithoutExtension(launcher), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            command = $"dotnet \"{entryAssemblyPath}\" {flag}";
        }
        else if (launcher.Contains(' ', StringComparison.Ordinal))
        {
            // PowerShell will not run a quoted path without its call operator.
            command = $"{(windows ? "& " : string.Empty)}\"{launcher}\" {flag}";
        }
        else
        {
            command = $"{launcher} {flag}";
        }

        return windows ? command : $"sudo {command}";
    }
}
