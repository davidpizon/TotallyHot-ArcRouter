using Microsoft.AspNetCore.Server.Kestrel.Core;
using TotallyHot.ArcRouter.Hosting;

namespace TotallyHot.ArcRouter.Tests.Hosting;

/// <summary>
/// Covers <see cref="KestrelBindAddress"/>'s mode resolution.
/// <see cref="KestrelServerOptions.Listen(System.Net.IPAddress, int)"/> and its dual-stack convenience
/// methods only record configuration - no socket is actually bound until
/// Kestrel starts - so these exercise every branch without needing a live listener or a free port,
/// unlike <see cref="Proxy.ProxyServerTests"/>'s end-to-end "loopback"/"any" coverage.
/// </summary>
public sealed class KestrelBindAddressTests
{
    [Theory]
    [InlineData("loopback")]
    [InlineData("any")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Listen_EphemeralPort_BindsSingleAddress_DoesNotThrow(string bindAddress)
    {
        var options = new KestrelServerOptions();

        var exception = Record.Exception(() => KestrelBindAddress.Listen(options: options, bindAddress: bindAddress, port: 0));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("loopback")]
    [InlineData("any")]
    [InlineData("127.0.0.1")]
    public void Listen_FixedPort_DoesNotThrow(string bindAddress)
    {
        var options = new KestrelServerOptions();

        // No socket is bound synchronously by KestrelServerOptions.Listen/ListenLocalhost/ListenAnyIP -
        // they only register configuration for Kestrel to apply on StartAsync - so an arbitrary fixed
        // port is safe here even if something else on the machine happens to be using it.
        var exception = Record.Exception(() => KestrelBindAddress.Listen(options: options, bindAddress: bindAddress, port: 34567));

        Assert.Null(exception);
    }

    [Fact]
    public void Listen_WithConfigureCallback_InvokesConfigure()
    {
        var options = new KestrelServerOptions();
        var configureCalled = false;

        KestrelBindAddress.Listen(options: options, bindAddress: "loopback", port: 34568,
            configure: _ => configureCalled = true);

        Assert.True(configureCalled);
    }

    [Fact]
    public void Listen_EphemeralPort_WithConfigureCallback_InvokesConfigure()
    {
        var options = new KestrelServerOptions();
        var configureCalled = false;

        KestrelBindAddress.Listen(options: options, bindAddress: "any", port: 0, configure: _ => configureCalled = true);

        Assert.True(configureCalled);
    }

    [Fact]
    public void Listen_InvalidBindAddress_ThrowsFormatException()
    {
        var options = new KestrelServerOptions();

        Assert.Throws<FormatException>(() =>
            KestrelBindAddress.Listen(options: options, bindAddress: "not-an-address", port: 34569));
    }

    [Fact]
    public void Listen_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            KestrelBindAddress.Listen(options: null!, bindAddress: "loopback", port: 0));
    }

    [Fact]
    public void Listen_NullBindAddress_Throws()
    {
        var options = new KestrelServerOptions();

        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException specifically for null -
        // a subtype of ArgumentException, but Assert.Throws<T> requires an exact type match, so this is
        // asserted separately from the empty/whitespace cases below.
        Assert.Throws<ArgumentNullException>(() =>
            KestrelBindAddress.Listen(options: options, bindAddress: null!, port: 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Listen_EmptyOrWhitespaceBindAddress_ThrowsArgumentException(string bindAddress)
    {
        var options = new KestrelServerOptions();

        Assert.Throws<ArgumentException>(() =>
            KestrelBindAddress.Listen(options: options, bindAddress: bindAddress, port: 0));
    }
}
