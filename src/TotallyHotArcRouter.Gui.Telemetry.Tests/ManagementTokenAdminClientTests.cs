using AwesomeAssertions;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Tests for <see cref="ManagementTokenAdminClient"/> since ADR-0020 made both RPCs require a one-operation
/// passkey authorization: the token must reach the wire in <c>authorization_token</c>, and a missing one must be
/// refused before any call is made.
/// </summary>
public class ManagementTokenAdminClientTests
{
    [Fact]
    public async Task GetTokenAsync_sends_the_authorization_token_and_returns_the_token()
    {
        var stub = new StubClient { Response = new Contract.ManagementTokenResponse { Token = "mgmt-token" } };

        var token = await new ManagementTokenAdminClient(stub)
            .GetTokenAsync("authz-get", TestContext.Current.CancellationToken);

        token.Should().Be("mgmt-token");
        stub.GetAuthorization.Should().Be("authz-get");
    }

    [Fact]
    public async Task RegenerateAsync_sends_the_authorization_token_and_returns_the_new_token()
    {
        var stub = new StubClient { Response = new Contract.ManagementTokenResponse { Token = "new-token" } };

        var token = await new ManagementTokenAdminClient(stub)
            .RegenerateAsync("authz-regen", TestContext.Current.CancellationToken);

        token.Should().Be("new-token");
        stub.RegenerateAuthorization.Should().Be("authz-regen");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_missing_authorization_is_refused_before_any_call(string? authorization)
    {
        var stub = new StubClient();
        var client = new ManagementTokenAdminClient(stub);

        var get = () => client.GetTokenAsync(authorization!, TestContext.Current.CancellationToken);
        var regenerate = () => client.RegenerateAsync(authorization!, TestContext.Current.CancellationToken);

        await get.Should().ThrowAsync<ArgumentException>();
        await regenerate.Should().ThrowAsync<ArgumentException>();
        stub.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_router_refusal_surfaces_with_the_routers_detail()
    {
        var stub = new StubClient
        {
            Failure = new RpcException(new Status(StatusCode.Unauthenticated,
                "Passkey verification is required for this operation."))
        };

        var ex = await Assert.ThrowsAsync<GrpcAdminException>(() =>
            new ManagementTokenAdminClient(stub).GetTokenAsync("spent", TestContext.Current.CancellationToken));

        ex.Message.Should().Be(
            "Could not read the management token: Passkey verification is required for this operation.");
        ex.IsUnavailable.Should().BeFalse();
    }

    private sealed class StubClient : Contract.ManagementTokenAdminService.ManagementTokenAdminServiceClient
    {
        public Contract.ManagementTokenResponse Response { get; init; } = new();
        public RpcException? Failure { get; init; }
        public string? GetAuthorization { get; private set; }
        public string? RegenerateAuthorization { get; private set; }
        public int Calls { get; private set; }

        private AsyncUnaryCall<Contract.ManagementTokenResponse> Reply()
        {
            Calls++;
            return new AsyncUnaryCall<Contract.ManagementTokenResponse>(
                responseAsync: Failure is null
                    ? Task.FromResult(Response)
                    : Task.FromException<Contract.ManagementTokenResponse>(Failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        public override AsyncUnaryCall<Contract.ManagementTokenResponse> GetManagementTokenAsync(
            Contract.GetManagementTokenRequest request, CallOptions options)
        {
            GetAuthorization = request.AuthorizationToken;
            return Reply();
        }

        public override AsyncUnaryCall<Contract.ManagementTokenResponse> RegenerateManagementTokenAsync(
            Contract.RegenerateManagementTokenRequest request, CallOptions options)
        {
            RegenerateAuthorization = request.AuthorizationToken;
            return Reply();
        }
    }
}
