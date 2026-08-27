using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Thrown when a routing-gate read or write call fails. Carries a message fit to render in a tray
/// notification rather than a raw <see cref="RpcException"/>, mirroring <see cref="RoutingModeAdminException"/>.
/// </summary>
public sealed class RoutingGateAdminException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RoutingGateAdminException"/> class.</summary>
    public RoutingGateAdminException(string message, Exception? innerException = null, bool isUnavailable = false)
        : base(message, innerException)
    {
        IsUnavailable = isUnavailable;
    }

    /// <summary>Gets whether the call failed because the router could not be reached.</summary>
    public bool IsUnavailable { get; }
}

/// <summary>
/// Client for the proxy's <c>RoutingGateAdminService</c> - the tray's "Enable Routing"/"Disable Routing"
/// toggle. Lives in this plain <c>net10.0</c> library rather than the Windows-only MAUI project so CI can
/// unit-test it, exactly like <c>RoutingModeAdminClient</c>.
/// </summary>
public sealed class RoutingGateAdminClient : IRoutingGateAdminClient, IDisposable
{
    private readonly Contract.RoutingGateAdminService.RoutingGateAdminServiceClient _client;
    private readonly IDisposable? _ownedChannel;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingGateAdminClient"/> class, creating and owning a
    /// channel to <paramref name="serverAddress"/>.
    /// </summary>
    public RoutingGateAdminClient(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        var channel = TelemetryChannelFactory.Create(serverAddress);
        _ownedChannel = channel;
        _client = new Contract.RoutingGateAdminService.RoutingGateAdminServiceClient(TelemetryChannelFactory.Authenticated(channel));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingGateAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    public RoutingGateAdminClient(Contract.RoutingGateAdminService.RoutingGateAdminServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedChannel = null;
    }

    /// <inheritdoc />
    public async Task<bool> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .GetRoutingGateAsync(new Contract.GetRoutingGateRequest(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Enabled;
        }
        catch (RpcException ex)
        {
            throw Wrap(ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .SetRoutingGateAsync(new Contract.SetRoutingGateRequest { Enabled = enabled }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Enabled;
        }
        catch (RpcException ex)
        {
            throw Wrap(ex);
        }
    }

    // Unavailable means the proxy isn't running, which is an ordinary state for a GUI that can outlive it -
    // so it gets a plain-language message rather than a gRPC status dump, and is flagged so the caller can
    // tell a dead connection from a rejected request without parsing the text. Mirrors RoutingModeAdminClient's Wrap.
    /// <summary>Wraps an <see cref="RpcException"/> into a <see cref="RoutingGateAdminException"/>, flagging router-unreachable errors distinctly.</summary>
    private static RoutingGateAdminException Wrap(RpcException ex) =>
        ex.StatusCode == StatusCode.Unavailable
            ? new RoutingGateAdminException("Could not reach the router: the router is not reachable.", ex, isUnavailable: true)
            : new RoutingGateAdminException($"Could not update the routing gate: {ex.Status.Detail}", ex);

    /// <inheritdoc />
    public void Dispose() => _ownedChannel?.Dispose();
}
