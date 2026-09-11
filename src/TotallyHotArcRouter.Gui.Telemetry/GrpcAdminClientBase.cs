using Grpc.Core;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Base for the gRPC admin clients in this namespace (<see cref="PriceSourceAdminClient"/>,
/// <see cref="ClusterModelAdminClient"/>, <see cref="BenchmarkDataAdminClient"/>, etc.): owns the
/// owned-channel-vs-injected-client constructor pair, channel disposal, and the "unavailable → friendly
/// message, else → server detail" exception-wrapping rule every one of them used to reimplement
/// identically. Each concrete client keeps its own RPC calls and DTO mapping - only this scaffolding
/// lives here.
/// </summary>
/// <typeparam name="TGeneratedClient">The generated gRPC client type this admin client wraps.</typeparam>
public abstract class GrpcAdminClientBase<TGeneratedClient> : IDisposable
{
    private readonly IDisposable? _ownedChannel;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcAdminClientBase{TGeneratedClient}"/> class, creating
    /// and owning a channel to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="serverAddress">The proxy's gRPC endpoint.</param>
    /// <param name="createClient">Constructs the generated client from the authenticated call invoker.</param>
    protected GrpcAdminClientBase(string serverAddress, Func<CallInvoker, TGeneratedClient> createClient)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        ArgumentNullException.ThrowIfNull(createClient);

        var channel = TelemetryChannelFactory.Create(serverAddress);
        _ownedChannel = channel;
        Client = createClient(TelemetryChannelFactory.Authenticated(channel));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcAdminClientBase{TGeneratedClient}"/> class over a
    /// caller-supplied generated client. The seam tests use to substitute a fake without a live server; the
    /// caller owns the channel's lifetime.
    /// </summary>
    protected GrpcAdminClientBase(TGeneratedClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Client = client;
        _ownedChannel = null;
    }

    /// <summary>Gets the generated gRPC client this admin client wraps.</summary>
    protected TGeneratedClient Client { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        _ownedChannel?.Dispose();
    }

    /// <summary>
    /// Wraps <paramref name="ex"/> into a <see cref="GrpcAdminException"/>: a plain-language,
    /// <see cref="GrpcAdminException.IsUnavailable"/>-flagged message of the form
    /// <c>"{action}: the router is not reachable."</c> when the router isn't reachable (an ordinary state
    /// for a GUI that can outlive it), or <c>"{action}: {server detail}"</c> otherwise - so callers can
    /// tell a dead connection from a rejected request without parsing the text.
    /// </summary>
    /// <param name="ex">The failed call's exception.</param>
    /// <param name="action">Describes the failed operation, e.g. <c>"Could not read the price sources"</c>.</param>
    protected static GrpcAdminException Wrap(RpcException ex, string action)
    {
        return Wrap(ex: ex, unavailableMessage: $"{action}: the router is not reachable.", action: action);
    }

    /// <summary>
    /// Wraps <paramref name="ex"/> like <see cref="Wrap(RpcException, string)"/>, but with an explicit
    /// <paramref name="unavailableMessage"/> independent of <paramref name="action"/> - for a client whose
    /// unreachable-router message doesn't follow the common <c>"{action}: the router is not reachable."</c>
    /// shape (e.g. <c>RoutingGateAdminClient</c>, whose actions describe individual calls but whose
    /// unavailable message is action-agnostic).
    /// </summary>
    protected static GrpcAdminException Wrap(RpcException ex, string unavailableMessage, string action)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex.StatusCode == StatusCode.Unavailable
            ? new GrpcAdminException(message: unavailableMessage, innerException: ex, isUnavailable: true)
            : new GrpcAdminException(message: $"{action}: {ex.Status.Detail}", innerException: ex);
    }
}
