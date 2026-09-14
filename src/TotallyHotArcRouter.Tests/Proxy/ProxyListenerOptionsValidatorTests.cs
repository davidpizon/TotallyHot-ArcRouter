using TotallyHot.ArcRouter.Mcp;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProxyListenerOptionsValidator"/>: port-range checks and the opt-in plain-HTTP
/// listener's cross-options port-collision checks (web GUI migration plan Phase P1, D6a).
/// </summary>
public sealed class ProxyListenerOptionsValidatorTests
{
    private static ProxyListenerOptionsValidator CreateValidator(
        int webInterfacePort = 5004, int mcpPort = 5003)
    {
        return new ProxyListenerOptionsValidator(
            webInterfaceOptions: new StaticOptionsMonitor<WebInterfaceOptions>(
                new WebInterfaceOptions { Port = webInterfacePort }),
            mcpOptions: new StaticOptionsMonitor<McpOptions>(new McpOptions { Port = mcpPort }));
    }

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = CreateValidator().Validate(name: null, new ProxyListenerOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_PlainHttpDisabled_IgnoresItsPort_EvenIfItWouldCollide()
    {
        var options = new ProxyListenerOptions
        {
            PlainHttp = new PlainHttpListenerOptions { Enabled = false, Port = 5001 }
        };

        var result = CreateValidator().Validate(name: null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(5001)] // collides with Port
    [InlineData(5002)] // collides with GrpcPort
    public void Validate_PlainHttpEnabled_CollidesWithProxyListenerPorts_Fails(int plainHttpPort)
    {
        var options = new ProxyListenerOptions
        {
            Port = 5001,
            GrpcPort = 5002,
            PlainHttp = new PlainHttpListenerOptions { Enabled = true, Port = plainHttpPort }
        };

        var result = CreateValidator().Validate(name: null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("collides", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PlainHttpEnabled_CollidesWithWebInterfacePort_Fails()
    {
        var options = new ProxyListenerOptions
        {
            PlainHttp = new PlainHttpListenerOptions { Enabled = true, Port = 5004 }
        };

        var result = CreateValidator(webInterfacePort: 5004).Validate(name: null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(WebInterfaceOptions), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PlainHttpEnabled_CollidesWithMcpPort_Fails()
    {
        var options = new ProxyListenerOptions
        {
            PlainHttp = new PlainHttpListenerOptions { Enabled = true, Port = 5003 }
        };

        var result = CreateValidator(mcpPort: 5003).Validate(name: null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(McpOptions), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PlainHttpEnabled_EphemeralPort_Fails()
    {
        var options = new ProxyListenerOptions
        {
            PlainHttp = new PlainHttpListenerOptions { Enabled = true, Port = 0 }
        };

        var result = CreateValidator().Validate(name: null, options);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_PortOutOfRange_Fails(int port)
    {
        var result = CreateValidator().Validate(name: null, new ProxyListenerOptions { Port = port });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Validate_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CreateValidator().Validate(name: null, options: null!));
    }
}
