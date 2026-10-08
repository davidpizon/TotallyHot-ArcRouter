using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The management-token read/rotate operations the System Settings window's "Copy MCP token / Regenerate"
/// row needs. An interface so the consuming store can be unit-tested against a fake without a live proxy
/// or a gRPC channel, mirroring <see cref="IRoutingGateAdminClient"/>.
/// </summary>
public interface IManagementTokenAdminClient
{
    /// <summary>
    /// Reads the router's current shared management token. The router refuses the call unless it carries
    /// a one-operation passkey authorization for <see cref="PasskeyOperations.GetManagementToken"/> (ADR-0020).
    /// </summary>
    /// <param name="authorizationToken">
    /// The single-use authorization from <see cref="IPasskeyAdminClient.FinishOneOperationAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The call failed, was not authorized, or the router is unreachable.</exception>
    Task<string> GetTokenAsync(string authorizationToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints and persists a fresh token, returning the confirmed new value. The router refuses the call
    /// unless it carries a one-operation passkey authorization for
    /// <see cref="PasskeyOperations.RegenerateManagementToken"/> (ADR-0020).
    /// </summary>
    /// <param name="authorizationToken">
    /// The single-use authorization from <see cref="IPasskeyAdminClient.FinishOneOperationAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The call failed, was not authorized, or the router is unreachable.</exception>
    Task<string> RegenerateAsync(string authorizationToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Client for the proxy's <c>ManagementTokenAdminService</c> - the System Settings window's "Copy MCP
/// token / Regenerate" row (web GUI migration plan Phase P9). Lives in this plain <c>net10.0</c> library
/// rather than a UI-framework project so CI can unit-test it, exactly like <see cref="RoutingGateAdminClient"/>.
/// </summary>
public sealed class ManagementTokenAdminClient
    : GrpcAdminClientBase<Contract.ManagementTokenAdminService.ManagementTokenAdminServiceClient>,
        IManagementTokenAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementTokenAdminClient"/> class over a shared,
    /// already-authenticated call invoker - see <see cref="IRouterChannelProvider"/>'s remarks.
    /// </summary>
    /// <param name="callInvoker">The shared call invoker - see <see cref="IRouterChannelProvider.CallInvoker"/>.</param>
    public ManagementTokenAdminClient(CallInvoker callInvoker)
        : base(new Contract.ManagementTokenAdminService.ManagementTokenAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementTokenAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    /// <param name="client">The generated client to wrap.</param>
    public ManagementTokenAdminClient(Contract.ManagementTokenAdminService.ManagementTokenAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<string> GetTokenAsync(string authorizationToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorizationToken);

        try
        {
            var response = await Client
                .GetManagementTokenAsync(
                    request: new Contract.GetManagementTokenRequest { AuthorizationToken = authorizationToken },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Token;
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not read the management token");
        }
    }

    /// <inheritdoc/>
    public async Task<string> RegenerateAsync(string authorizationToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorizationToken);

        try
        {
            var response = await Client
                .RegenerateManagementTokenAsync(
                    request: new Contract.RegenerateManagementTokenRequest { AuthorizationToken = authorizationToken },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Token;
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, action: "Could not regenerate the management token");
        }
    }
}
