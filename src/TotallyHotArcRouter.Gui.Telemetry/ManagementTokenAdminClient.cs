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
    /// <summary>Reads the router's current shared management token.</summary>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Mints and persists a fresh token, returning the confirmed new value.</summary>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<string> RegenerateAsync(CancellationToken cancellationToken = default);
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

    /// <inheritdoc/>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetManagementTokenAsync(request: new Contract.GetManagementTokenRequest(),
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
    public async Task<string> RegenerateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .RegenerateManagementTokenAsync(request: new Contract.RegenerateManagementTokenRequest(),
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
