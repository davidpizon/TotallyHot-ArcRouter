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
        int webInterfacePort = 47104, int mcpPort = 47103)
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
            PlainHttp = new PlainHttpListenerOptions { Enabled = false, Port = 47101 }
        };

        var result = CreateValidator().Validate(name: null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(47101)] // collides with Port
    public void Validate_PlainHttpEnabled_CollidesWithProxyListenerPorts_Fails(int plainHttpPort)
    {
        var options = new ProxyListenerOptions
        {
            Port = 47101,
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
            PlainHttp = new PlainHttpListenerOptions { Enabled = true, Port = 47104 }
        };

        var result = CreateValidator(webInterfacePort: 47104).Validate(name: null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(WebInterfaceOptions), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PlainHttpEnabled_CollidesWithMcpPort_Fails()
    {
        var options = new ProxyListenerOptions
        {
            PlainHttp = new PlainHttpListenerOptions { Enabled = true, Port = 47103 }
        };

        var result = CreateValidator(mcpPort: 47103).Validate(name: null, options);

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
        Assert.Throws<ArgumentNullException>(() =>
            CreateValidator().Validate(name: null, options: (ProxyListenerOptions)null!));
    }

    [Fact]
    public void Validate_ProxyPortCollidesWithWebInterfacePort_FailsEvenWithPlainHttpDisabled()
    {
        // Regression coverage for a real bug: every collision check used to be nested inside
        // `if (options.PlainHttp.Enabled)`, so with PlainHttp off (the default) a collision between the
        // two always-active TLS listeners went uncaught entirely.
        var options = new ProxyListenerOptions { Port = 47104 };

        var result = CreateValidator(webInterfacePort: 47104).Validate(name: null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(WebInterfaceOptions), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProxyPortCollidesWithMcpPort_WhenMcpEnabled_FailsEvenWithPlainHttpDisabled()
    {
        var options = new ProxyListenerOptions { Port = 47103 };

        var result = new ProxyListenerOptionsValidator(
                webInterfaceOptions: new StaticOptionsMonitor<WebInterfaceOptions>(new WebInterfaceOptions { Port = 47104 }),
                mcpOptions: new StaticOptionsMonitor<McpOptions>(new McpOptions { Enabled = true, Port = 47103 }))
            .Validate(name: null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(McpOptions), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProxyPortCollidesWithMcpPort_WhenMcpDisabled_Succeeds()
    {
        // A real bug fixed alongside the above: Mcp collisions must be gated on McpOptions.Enabled, since
        // a disabled McpHostedService never binds McpOptions.Port at all - that port is genuinely free.
        var options = new ProxyListenerOptions { Port = 47103 };

        var result = new ProxyListenerOptionsValidator(
                webInterfaceOptions: new StaticOptionsMonitor<WebInterfaceOptions>(new WebInterfaceOptions { Port = 47104 }),
                mcpOptions: new StaticOptionsMonitor<McpOptions>(new McpOptions { Enabled = false, Port = 47103 }))
            .Validate(name: null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WebInterfacePortCollidesWithMcpPort_WhenMcpEnabled_Fails()
    {
        var result = new ProxyListenerOptionsValidator(
                webInterfaceOptions: new StaticOptionsMonitor<WebInterfaceOptions>(new WebInterfaceOptions { Port = 47103 }),
                mcpOptions: new StaticOptionsMonitor<McpOptions>(new McpOptions { Enabled = true, Port = 47103 }))
            .Validate(name: null, new ProxyListenerOptions { Port = 47101 });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!,
            f => f.Contains(nameof(WebInterfaceOptions), StringComparison.Ordinal) &&
                 f.Contains(nameof(McpOptions), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EphemeralPorts_NeverCollide()
    {
        var result = new ProxyListenerOptionsValidator(
                webInterfaceOptions: new StaticOptionsMonitor<WebInterfaceOptions>(new WebInterfaceOptions { Port = 0 }),
                mcpOptions: new StaticOptionsMonitor<McpOptions>(new McpOptions { Enabled = true, Port = 0 }))
            .Validate(name: null, new ProxyListenerOptions { Port = 0 });

        Assert.True(result.Succeeded);
    }

}

/// <summary>
/// Covers <see cref="PortRangeOptionsValidator"/>: the dependency-free port-range checks for
/// <see cref="WebInterfaceOptions"/> and <see cref="McpOptions"/>, kept out of
/// <see cref="ProxyListenerOptionsValidator"/> to avoid a circular DI dependency (see that class's own
/// remarks).
/// </summary>
public sealed class PortRangeOptionsValidatorTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void ValidateWebInterfaceOptions_PortOutOfRange_Fails(int port)
    {
        var result = new PortRangeOptionsValidator().Validate(name: null, new WebInterfaceOptions { Port = port });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void ValidateWebInterfaceOptions_EphemeralPort_Succeeds()
    {
        var result = new PortRangeOptionsValidator().Validate(name: null, new WebInterfaceOptions { Port = 0 });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void ValidateMcpOptions_PortOutOfRange_Fails(int port)
    {
        var result = new PortRangeOptionsValidator().Validate(name: null, new McpOptions { Port = port });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void ValidateMcpOptions_EphemeralPort_Succeeds()
    {
        var result = new PortRangeOptionsValidator().Validate(name: null, new McpOptions { Port = 0 });

        Assert.True(result.Succeeded);
    }
}
