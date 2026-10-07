using AwesomeAssertions;
using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementTokenAdminGrpcService"/>: it must report
/// <see cref="IManagementTokenProvider.CurrentToken"/> and, on a regenerate call, both rotate the token
/// through the shared provider and echo back the confirmed new value - but only behind a one-operation
/// passkey authorization (ADR-0020).
/// </summary>
public sealed class ManagementTokenAdminGrpcServiceTests
{
    private static ServerCallContext CreateContext() => PasskeyGateHarness.Context(
        cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task GetManagementToken_WithAuthorization_ReportsTheProvidersCurrentToken()
    {
        var provider = new FakeManagementTokenProvider("initial-token");
        var harness = PasskeyGateHarness.Create();
        var service = new ManagementTokenAdminGrpcService(provider, harness.Gate);
        var authorization = harness.OneOperations.Issue(
            GatedOperation.GetManagementToken, GatedOperation.GetManagementTokenParameters());

        var response = await service.GetManagementToken(
            request: new Contract.GetManagementTokenRequest { AuthorizationToken = authorization },
            context: CreateContext());

        response.Token.Should().Be("initial-token");
    }

    [Fact]
    public async Task GetManagementToken_WithoutAuthorization_IsRefused()
    {
        var service = new ManagementTokenAdminGrpcService(
            new FakeManagementTokenProvider("initial-token"), PasskeyGateHarness.Create().Gate);

        var act = () => service.GetManagementToken(
            request: new Contract.GetManagementTokenRequest(), context: CreateContext());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task GetManagementToken_AuthorizationIsSingleUse()
    {
        var harness = PasskeyGateHarness.Create();
        var service = new ManagementTokenAdminGrpcService(new FakeManagementTokenProvider("t"), harness.Gate);
        var request = new Contract.GetManagementTokenRequest
        {
            AuthorizationToken = harness.OneOperations.Issue(
                GatedOperation.GetManagementToken, GatedOperation.GetManagementTokenParameters())
        };

        await service.GetManagementToken(request, CreateContext());
        var act = () => service.GetManagementToken(request, CreateContext());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task GetManagementToken_WithoutEnrolledPasskey_IsRefusedAsFailedPrecondition()
    {
        var service = new ManagementTokenAdminGrpcService(
            new FakeManagementTokenProvider("t"), PasskeyGateHarness.Create(enrolled: false).Gate);

        var act = () => service.GetManagementToken(
            request: new Contract.GetManagementTokenRequest { AuthorizationToken = "anything" },
            context: CreateContext());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task RegenerateManagementToken_WithAuthorization_RotatesTheProvidersToken_AndEchoesTheConfirmedValue()
    {
        var provider = new FakeManagementTokenProvider("initial-token");
        var harness = PasskeyGateHarness.Create();
        var service = new ManagementTokenAdminGrpcService(provider, harness.Gate);
        var authorization = harness.OneOperations.Issue(
            GatedOperation.RegenerateManagementToken, GatedOperation.RegenerateManagementTokenParameters());

        var response = await service.RegenerateManagementToken(
            request: new Contract.RegenerateManagementTokenRequest { AuthorizationToken = authorization },
            context: CreateContext());

        provider.CurrentToken.Should().Be(response.Token);
        provider.CurrentToken.Should().NotBe("initial-token");
    }

    [Fact]
    public async Task RegenerateManagementToken_WithAnotherOperationsAuthorization_DoesNotRotate()
    {
        var provider = new FakeManagementTokenProvider("initial-token");
        var harness = PasskeyGateHarness.Create();
        var service = new ManagementTokenAdminGrpcService(provider, harness.Gate);
        var authorization = harness.OneOperations.Issue(
            GatedOperation.GetManagementToken, GatedOperation.GetManagementTokenParameters());

        var act = () => service.RegenerateManagementToken(
            request: new Contract.RegenerateManagementTokenRequest { AuthorizationToken = authorization },
            context: CreateContext());

        await act.Should().ThrowAsync<RpcException>();
        provider.CurrentToken.Should().Be("initial-token");
    }

    [Fact]
    public void Constructor_ThrowsOnNullTokenProvider()
    {
        var act = () => new ManagementTokenAdminGrpcService(null!, PasskeyGateHarness.Create().Gate);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ThrowsOnNullContentGate()
    {
        var act = () => new ManagementTokenAdminGrpcService(new FakeManagementTokenProvider("t"), null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
