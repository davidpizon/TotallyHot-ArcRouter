using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Client for the proxy's <c>RoutingGateAdminService</c> - the tray's "Enable Routing"/"Disable Routing"
/// toggle. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project so CI can
/// unit-test it, exactly like <c>RoutingModeAdminClient</c>.
/// </summary>
public sealed class RoutingGateAdminClient
    : GrpcAdminClientBase<Contract.RoutingGateAdminService.RoutingGateAdminServiceClient>,
        IRoutingGateAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingGateAdminClient"/> class over a shared, already-
    /// authenticated call invoker (web GUI migration plan Phase P5a) - see
    /// <see cref="IRouterChannelProvider"/>'s remarks for why production goes through this shared
    /// invoker rather than a channel of its own. The
    /// caller owns the invoker's underlying channel.
    /// </summary>
    /// <param name="callInvoker">The shared call invoker - see <see cref="IRouterChannelProvider.CallInvoker"/>.</param>
    public RoutingGateAdminClient(CallInvoker callInvoker)
        : base(new Contract.RoutingGateAdminService.RoutingGateAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingGateAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public RoutingGateAdminClient(Contract.RoutingGateAdminService.RoutingGateAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<bool> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetRoutingGateAsync(request: new Contract.GetRoutingGateRequest(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Enabled;
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, unavailableMessage: "Could not reach the router: the router is not reachable.",
                action: "Could not read the routing gate");
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SetAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .SetRoutingGateAsync(request: new Contract.SetRoutingGateRequest { Enabled = enabled },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Enabled;
        }
        catch (RpcException ex)
        {
            throw Wrap(ex: ex, unavailableMessage: "Could not reach the router: the router is not reachable.",
                action: "Could not update the routing gate");
        }
    }
}