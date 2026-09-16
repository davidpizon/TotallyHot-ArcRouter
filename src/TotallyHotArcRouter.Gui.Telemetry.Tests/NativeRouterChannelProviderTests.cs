using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="NativeRouterChannelProvider"/>: construction never connects (so this is safe to
/// run without a live proxy), it reports the address it was given, and its
/// <see cref="NativeRouterChannelProvider.CallInvoker"/> is usable after disposal-unrelated construction.
/// </summary>
public sealed class NativeRouterChannelProviderTests
{
    [Fact]
    public void ServerAddress_ReflectsTheConstructorArgument()
    {
        using var provider = new NativeRouterChannelProvider("https://localhost:65111");

        provider.ServerAddress.Should().Be("https://localhost:65111");
    }

    [Fact]
    public void DefaultConstructor_UsesTheDefaultServerAddress()
    {
        using var provider = new NativeRouterChannelProvider();

        provider.ServerAddress.Should().Be(TelemetryChannelFactory.DefaultServerAddress);
    }

    [Fact]
    public void CallInvoker_IsNotNull()
    {
        using var provider = new NativeRouterChannelProvider("https://localhost:65111");

        provider.CallInvoker.Should().NotBeNull();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var provider = new NativeRouterChannelProvider("https://localhost:65111");

        var act = provider.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_NullServerAddress_Throws()
    {
        var act = () => new NativeRouterChannelProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
