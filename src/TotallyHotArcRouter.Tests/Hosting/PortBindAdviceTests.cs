using TotallyHot.ArcRouter.Hosting;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="PortBindAdvice"/>: the port is recovered from Kestrel's message so the suggested
/// commands name the real port, and an unrecognised message degrades to a placeholder instead of throwing.
/// </summary>
public sealed class PortBindAdviceTests
{
    [Theory]
    [InlineData("Failed to bind to address https://127.0.0.1:47101: address already in use.", 47101)]
    [InlineData("Failed to bind to address http://[::1]:47105: address already in use.", 47105)]
    public void TryGetPort_KestrelMessage_ReturnsPort(string message, int expected)
    {
        Assert.Equal(expected, PortBindAdvice.TryGetPort(message));
    }

    [Fact]
    public void TryGetPort_UnrecognisedMessage_ReturnsNull()
    {
        Assert.Null(PortBindAdvice.TryGetPort("something else entirely"));
    }

    [Fact]
    public void Suggestions_NamesThePortAndTheSetting()
    {
        var text = PortBindAdvice.Suggestions(
            kestrelMessage: "Failed to bind to address https://127.0.0.1:47101: address already in use.",
            settingHint: "Proxy:Port", additionalSuggestion: null, windows: true);

        Assert.Contains("-LocalPort 47101", text, StringComparison.Ordinal);
        Assert.Contains("startport=47101", text, StringComparison.Ordinal);
        Assert.Contains("Proxy:Port", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggestions_UnrecognisedMessage_UsesPlaceholder()
    {
        var text = PortBindAdvice.Suggestions(kestrelMessage: "odd", settingHint: "Mcp:Port", additionalSuggestion: null, windows: true);

        Assert.Contains("<port>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggestions_Windows_WarnsAboutWinNatAndNamesPowerShell()
    {
        var text = PortBindAdvice.Suggestions("Failed to bind to address https://127.0.0.1:47101: x", "Proxy:Port", null, windows: true);

        Assert.Contains("interrupts every WSL2, Hyper-V and container", text, StringComparison.Ordinal);
        Assert.Contains("PowerShell prompt (not cmd.exe)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggestions_NonWindows_OmitsWindowsCommands_AndKeepsChangePortAndExtra()
    {
        var text = PortBindAdvice.Suggestions("Failed to bind to address https://127.0.0.1:47101: x", "Mcp:Port", "Set Mcp:Enabled=false.", windows: false);

        Assert.DoesNotContain("winnat", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-NetTCPConnection", text, StringComparison.Ordinal);
        Assert.Contains("1. Move the listener to a free port with Mcp:Port", text, StringComparison.Ordinal);
        Assert.Contains("2. Set Mcp:Enabled=false.", text, StringComparison.Ordinal);
    }
}
