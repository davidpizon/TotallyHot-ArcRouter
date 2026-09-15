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
            secFetchSitePresent: true,
            expectedOrigin: "https://localhost:5004"));
    }

    [Fact]
    public void IsOriginAllowed_ForeignOrigin_ReturnsFalse()
    {
        Assert.False(LoopbackRequestGuard.IsOriginAllowed(
            origin: "https://evil.example",
            secFetchSitePresent: true,
            expectedOrigin: "https://localhost:5004"));
    }

    [Fact]
    public void IsOriginAllowed_AbsentOriginWithNoSecFetchSite_ReturnsTrue()
    {
        Assert.True(LoopbackRequestGuard.IsOriginAllowed(
            origin: null,
            secFetchSitePresent: false,
            expectedOrigin: "https://localhost:5004"));
    }

    [Fact]
    public void IsOriginAllowed_AbsentOriginWithSecFetchSitePresent_ReturnsFalse()
    {
        // A browser request that stripped Origin but still carries Sec-Fetch-Site is not the native/CLI
        // case this absence-tolerance exists for - treat it as untrusted.
        Assert.False(LoopbackRequestGuard.IsOriginAllowed(
            origin: null,
            secFetchSitePresent: true,
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
