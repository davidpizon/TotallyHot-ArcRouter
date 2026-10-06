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
            settingHint: "Proxy:Port");

        Assert.Contains("-LocalPort 47101", text, StringComparison.Ordinal);
        Assert.Contains("startport=47101", text, StringComparison.Ordinal);
        Assert.Contains("Proxy:Port", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggestions_UnrecognisedMessage_UsesPlaceholder()
    {
        var text = PortBindAdvice.Suggestions(kestrelMessage: "odd", settingHint: "Mcp:Port");

        Assert.Contains("<port>", text, StringComparison.Ordinal);
    }
}
