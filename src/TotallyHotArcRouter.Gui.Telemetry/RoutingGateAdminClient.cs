using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a routing-gate read or write call fails. Carries a message fit to render in a tray
/// notification rather than a raw <see cref="RpcException"/>, mirroring <see cref="RoutingModeAdminException"/>.
/// See <see cref="GrpcAdminException.IsUnavailable"/>'s remarks.
/// </summary>
public sealed class RoutingGateAdminException : GrpcAdminException
{
    /// <summary>Initializes a new instance of the <see cref="RoutingGateAdminException"/> class.</summary>
    public RoutingGateAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message, innerException, isUnavailable)
    {
    }
}

/// <summary>
/// Client for the proxy's <c>RoutingGateAdminService</c> - the tray's "Enable Routing"/"Disable Routing"
/// toggle. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project so CI can
/// unit-test it, exactly like <c>RoutingModeAdminClient</c>.
/// </summary>
public sealed class RoutingGateAdminClient
    : GrpcAdminClientBase<Contract.RoutingGateAdminService.RoutingGateAdminServiceClient, RoutingGateAdminException>,
      IRoutingGateAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingGateAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public RoutingGateAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(serverAddress, callInvoker => new Contract.RoutingGateAdminService.RoutingGateAdminServiceClient(callInvoker))
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

    /// <inheritdoc />
    public async Task<bool> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .GetRoutingGateAsync(new Contract.GetRoutingGateRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Enabled;
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not reach the router: the router is not reachable.", "Could not update the routing gate");
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Client
                .SetRoutingGateAsync(new Contract.SetRoutingGateRequest { Enabled = enabled }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Enabled;
        }
        catch (RpcException ex)
        {
            throw Wrap(ex, "Could not reach the router: the router is not reachable.", "Could not update the routing gate");
        }
    }

    /// <inheritdoc />
    protected override RoutingGateAdminException CreateException(string message, Exception? innerException, bool isUnavailable) =>
        new(message, innerException, isUnavailable);
}
