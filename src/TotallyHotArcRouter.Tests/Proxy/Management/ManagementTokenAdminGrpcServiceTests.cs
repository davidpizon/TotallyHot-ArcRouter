using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Testing;
using TotallyHot.ArcRouter.Proxy.Management;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementTokenAdminGrpcService"/>: it must report
/// <see cref="IManagementTokenProvider.CurrentToken"/> and, on a regenerate call, both rotate the token
/// through the shared provider and echo back the confirmed new value.
/// </summary>
public sealed class ManagementTokenAdminGrpcServiceTests
{
    private static ServerCallContext CreateContext()
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: TestContext.Current.CancellationToken,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

    [Fact]
    public async Task GetManagementToken_ReportsTheProvidersCurrentToken()
    {
        var provider = new FakeManagementTokenProvider("initial-token");
        var service = new ManagementTokenAdminGrpcService(provider);

        var response = await service.GetManagementToken(request: new Contract.GetManagementTokenRequest(),
            context: CreateContext());

        response.Token.Should().Be("initial-token");
    }

    [Fact]
    public async Task RegenerateManagementToken_RotatesTheProvidersToken_AndEchoesTheConfirmedValue()
    {
        var provider = new FakeManagementTokenProvider("initial-token");
        var service = new ManagementTokenAdminGrpcService(provider);

        var response = await service.RegenerateManagementToken(
            request: new Contract.RegenerateManagementTokenRequest(), context: CreateContext());

        provider.CurrentToken.Should().Be(response.Token);
        provider.CurrentToken.Should().NotBe("initial-token");
    }

    [Fact]
    public void Constructor_ThrowsOnNullTokenProvider()
    {
        var act = () => new ManagementTokenAdminGrpcService(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
