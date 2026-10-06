using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// Builds the operator-facing suggestions logged when a listener cannot bind its port. The proxy and the
/// MCP endpoint hit the same environmental failure for the same reasons, so the diagnosis lives here once
/// rather than as two long, drifting string literals.
/// </summary>
/// <remarks>
/// The advice lists causes instead of naming one: another router instance or the installed Windows
/// service is only one possibility, and Windows also answers <c>EADDRINUSE</c> for ports reserved by the
/// Host Network Service (Hyper-V, WSL2, container networking), which appear in neither <c>netstat</c> nor
/// <c>netsh interface ipv4 show excludedportrange</c>. That is why the suggestions include a bind probe
/// and the <c>winnat</c> reset rather than just "find the process on that port".
/// </remarks>
internal static partial class PortBindAdvice
{
    [GeneratedRegex(@":(?<port>\d{1,5}):\s", RegexOptions.CultureInvariant)]
    private static partial Regex PortInMessage();

    /// <summary>
    /// Extracts the port Kestrel reported it could not bind from its exception message
    /// (<c>"Failed to bind to address https://127.0.0.1:47101: address already in use."</c>).
    /// </summary>
    /// <param name="kestrelMessage">The bind exception's <see cref="Exception.Message"/>.</param>
    /// <returns>The port, or <see langword="null"/> when the message has an unexpected shape.</returns>
    public static int? TryGetPort(string kestrelMessage)
    {
        var match = PortInMessage().Match(kestrelMessage);
        return match.Success && int.TryParse(match.Groups["port"].Value, out var port) ? port : null;
    }

    /// <summary>
    /// Formats the suggestions for a port that is already in use. Passed to the logger as a structured
    /// property so the log message template stays a static literal. The Windows-only diagnosis (port owner
    /// lookup, reserved ranges, the WinNAT reset) is included only when running on Windows; the change-port
    /// and optional-disable advice applies everywhere.
    /// </summary>
    /// <param name="kestrelMessage">The bind exception's message; used to name the exact port in the commands.</param>
    /// <param name="settingHint">
    /// The configuration key(s) that move the failing listener, for example <c>"Proxy:Port"</c>, shown in
    /// the suggestion that overrides the port.
    /// </param>
    /// <param name="additionalSuggestion">An extra, caller-specific last suggestion (for example "disable the endpoint"), or <see langword="null"/>.</param>
    /// <returns>A multi-line, numbered list of things to try, most likely first.</returns>
    public static string Suggestions(string kestrelMessage, string settingHint, string? additionalSuggestion = null)
    {
        return Suggestions(kestrelMessage, settingHint, additionalSuggestion, OperatingSystem.IsWindows());
    }

    /// <summary>
    /// <see cref="Suggestions(string, string, string?)"/> with the platform passed in, so tests can cover
    /// both branches on any host.
    /// </summary>
    /// <param name="kestrelMessage">The bind exception's message.</param>
    /// <param name="settingHint">The configuration key(s) that move the failing listener.</param>
    /// <param name="additionalSuggestion">An extra, caller-specific last suggestion, or <see langword="null"/>.</param>
    /// <param name="windows">Whether to include the Windows-only diagnosis steps.</param>
    /// <returns>A multi-line, numbered list of things to try, most likely first.</returns>
    internal static string Suggestions(string kestrelMessage, string settingHint, string? additionalSuggestion, bool windows)
    {
        var port = TryGetPort(kestrelMessage)?.ToString() ?? "<port>";
        List<string> steps = [];

        if (windows)
        {
            steps.Add(
                $"Another ArcRouter instance or the installed Windows service may hold it: Get-NetTCPConnection -LocalPort {port} -State Listen | Select OwningProcess" + Environment.NewLine +
                "     (nothing listed? the port is probably reserved, not listened on - continue below).");
            steps.Add(
                "Windows (Hyper-V, WSL2, containers) can reserve whole TCP ranges that netstat and 'netsh interface ipv4 show excludedportrange' do not show." + Environment.NewLine +
                "     Warning: stopping WinNAT interrupts every WSL2, Hyper-V and container connection that uses Windows NAT, machine-wide, until it restarts." + Environment.NewLine +
                "     From an elevated PowerShell prompt (not cmd.exe): net stop winnat; netsh int ipv4 add excludedportrange protocol=tcp startport=" + port + " numberofports=1; net start winnat");
        }

        steps.Add($"Move the listener to a free port with {settingHint} in appsettings.local.json in the machine-shared data directory (it survives MSI upgrades), then point clients at the new port.");
        if (additionalSuggestion is not null) steps.Add(additionalSuggestion);

        return string.Join(separator: Environment.NewLine,
            values: steps.Select((step, index) => $"  {index + 1}. {step}"));
    }
}
