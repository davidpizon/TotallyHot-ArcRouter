using System.Net;
using TotallyHot.ArcRouter.Proxy.Auth;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth;

/// <summary>
/// Covers <see cref="LoopbackRequestGuard"/>'s three pure checks against ADR-0012's exit matrix: Host
/// allowlist, Origin same-origin-or-absent, and loopback remote-IP detection with IPv4-mapped IPv6
/// normalized.
/// </summary>
public sealed class LoopbackRequestGuardTests
{
    private static readonly IReadOnlyList<string> DefaultAllowedHosts = ["localhost", "127.0.0.1", "[::1]"];

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public void IsHostAllowed_AllowedHost_ReturnsTrue(string host)
    {
        Assert.True(LoopbackRequestGuard.IsHostAllowed(host, DefaultAllowedHosts));
    }

    [Fact]
    public void IsHostAllowed_AttackerHost_ReturnsFalse()
    {
        Assert.False(LoopbackRequestGuard.IsHostAllowed("attacker.test", DefaultAllowedHosts));
    }

    [Fact]
    public void IsHostAllowed_NullOrEmptyHost_ReturnsFalse()
    {
        Assert.False(LoopbackRequestGuard.IsHostAllowed(null, DefaultAllowedHosts));
        Assert.False(LoopbackRequestGuard.IsHostAllowed("", DefaultAllowedHosts));
    }

    [Fact]
    public void IsHostAllowed_IsCaseInsensitive()
    {
        Assert.True(LoopbackRequestGuard.IsHostAllowed("LOCALHOST", DefaultAllowedHosts));
    }

    [Fact]
    public void IsOriginAllowed_MatchingOrigin_ReturnsTrue()
    {
        Assert.True(LoopbackRequestGuard.IsOriginAllowed(
            origin: "https://localhost:5004",
            secFetchSite: "same-origin",
            expectedOrigin: "https://localhost:5004"));
    }

    [Fact]
    public void IsOriginAllowed_ForeignOrigin_ReturnsFalse()
    {
        Assert.False(LoopbackRequestGuard.IsOriginAllowed(
            origin: "https://evil.example",
            secFetchSite: "cross-site",
            expectedOrigin: "https://localhost:5004"));
    }

    [Fact]
    public void IsOriginAllowed_AbsentOriginWithNoSecFetchSite_ReturnsTrue()
    {
        // The native/CLI case: neither header is sent at all.
        Assert.True(LoopbackRequestGuard.IsOriginAllowed(
            origin: null,
            secFetchSite: null,
            expectedOrigin: "https://localhost:5004"));
    }

    [Fact]
    public void IsOriginAllowed_AbsentOriginWithSecFetchSiteNone_ReturnsTrue()
    {
        // A real top-level browser navigation (typed URL, bookmark) never sends Origin, but a modern
        // browser still attaches Sec-Fetch-Site: none to it - this must not be rejected (a real bug this
        // regression test pins: the dashboard's own page load used to 403 for exactly this reason).
        Assert.True(LoopbackRequestGuard.IsOriginAllowed(
            origin: null,
            secFetchSite: "none",
            expectedOrigin: "https://localhost:5004"));
    }

    [Fact]
    public void IsOriginAllowed_AbsentOriginWithSecFetchSiteSameOrigin_ReturnsTrue()
    {
        // A same-origin subresource GET (an <img>/<script> the dashboard's own page loads) commonly
        // carries no Origin header either.
        Assert.True(LoopbackRequestGuard.IsOriginAllowed(
            origin: null,
            secFetchSite: "same-origin",
            expectedOrigin: "https://localhost:5004"));
    }

    [Fact]
    public void IsOriginAllowed_AbsentOriginWithSecFetchSiteCrossSite_ReturnsFalse()
    {
        // The actual CSRF vector this check exists to stop: a plain, Origin-less GET (an <img>/<a> tag)
        // issued from another site's page against this host.
        Assert.False(LoopbackRequestGuard.IsOriginAllowed(
            origin: null,
            secFetchSite: "cross-site",
            expectedOrigin: "https://localhost:5004"));
    }

    [Fact]
    public void IsLoopback_Ipv4Loopback_ReturnsTrue()
    {
        Assert.True(LoopbackRequestGuard.IsLoopback(IPAddress.Parse("127.0.0.1")));
    }

    [Fact]
    public void IsLoopback_Ipv6Loopback_ReturnsTrue()
    {
        Assert.True(LoopbackRequestGuard.IsLoopback(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void IsLoopback_Ipv4MappedIpv6Loopback_ReturnsTrue()
    {
        var mapped = IPAddress.Parse("127.0.0.1").MapToIPv6();
        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.True(LoopbackRequestGuard.IsLoopback(mapped));
    }

    [Fact]
    public void IsLoopback_RemoteAddress_ReturnsFalse()
    {
        Assert.False(LoopbackRequestGuard.IsLoopback(IPAddress.Parse("203.0.113.5")));
    }

    [Fact]
    public void IsLoopback_Null_ReturnsFalse()
    {
        Assert.False(LoopbackRequestGuard.IsLoopback(null));
    }
}
