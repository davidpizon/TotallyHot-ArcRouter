using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProviderUrlBuilder.IsUnencryptedNonLoopbackUpstream"/> - the D6b classifier (web GUI
/// migration plan Phase P7) deciding whether a provider's configured base URL warrants the
/// "unencrypted upstream" warning: <c>http://</c> reaching somewhere other than this machine.
/// </summary>
public class ProviderUrlBuilderUpstreamHttpTests
{
    [Theory]
    // Remote http:// - the condition this classifier exists to catch.
    [InlineData("http://example.com", true)]
    [InlineData("http://api.example.com:8080/v1", true)]
    [InlineData("http://203.0.113.5/v1", true)]
    // Loopback http:// - local runtimes (Ollama, LM Studio) that only speak plain HTTP. Never flagged.
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("http://LOCALHOST:1234/v1", false)]
    [InlineData("http://127.0.0.1:1234/v1", false)]
    [InlineData("http://[::1]:11434/v1", false)]
    // https:// is never flagged, loopback or not - the scheme itself is the whole condition.
    [InlineData("https://example.com", false)]
    [InlineData("https://localhost:11434/v1", false)]
    // A remote loopback-looking hostname that is not actually localhost/127.0.0.1/::1 is still flagged.
    [InlineData("http://127.0.0.1.example.com", true)]
    // Unparsable input is not flagged - validation elsewhere owns rejecting a malformed base URL.
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void IsUnencryptedNonLoopbackUpstream_ClassifiesAsExpected(string baseUrl, bool expected)
    {
        var actual = ProviderUrlBuilder.IsUnencryptedNonLoopbackUpstream(baseUrl);

        Assert.Equal(expected, actual);
    }
}
