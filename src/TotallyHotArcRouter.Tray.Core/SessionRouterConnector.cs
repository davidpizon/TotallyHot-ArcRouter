using System.Diagnostics.CodeAnalysis;
using Grpc.Core;
using Grpc.Net.Client;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Tray;

/// <summary>
/// Establishes an ADR-0012 loopback-session-authenticated connection to the router - the tray's auth
/// mechanism (web GUI migration plan Phase P8), in place of the shared <c>x-admin-token</c> the MAUI GUI
/// still uses. An interface so <see cref="RouterConnectionSupervisor"/> can be tested against a fake
/// connector without a live router.
/// </summary>
public interface ISessionRouterConnector
{
    /// <summary>
    /// Issues a fresh session (<c>POST {serverAddress}/auth/session</c>) and returns a channel provider
    /// authenticated by it.
    /// </summary>
    /// <param name="serverAddress">The router's TLS endpoint.</param>
    /// <param name="cancellationToken">Cancels the session request.</param>
    /// <exception cref="System.Net.Http.HttpRequestException">
    /// The session request failed - the router is unreachable, or refused the loopback fast path.
    /// </exception>
    Task<IRouterChannelProvider> ConnectAsync(string serverAddress, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production <see cref="ISessionRouterConnector"/>: wraps <see cref="TelemetryChannelFactory.CreateSessionAuthenticatedAsync"/>.
/// </summary>
/// <remarks>
/// Excluded from coverage: this type is a one-call wrapper around
/// <see cref="TelemetryChannelFactory.CreateSessionAuthenticatedAsync"/> (tested in Gui.Telemetry).
/// <see cref="ISessionRouterConnector"/> is the seam <c>RouterConnectionSupervisorTests</c> use so they
/// never open a live session.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class SessionRouterConnector : ISessionRouterConnector
{
    /// <inheritdoc/>
    public async Task<IRouterChannelProvider> ConnectAsync(string serverAddress,
        CancellationToken cancellationToken = default)
    {
        var channel = await TelemetryChannelFactory
            .CreateSessionAuthenticatedAsync(serverAddress: serverAddress, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new SessionRouterChannelProvider(channel, serverAddress);
    }
}

/// <summary>
/// The <see cref="IRouterChannelProvider"/> a successful <see cref="SessionRouterConnector"/> connection
/// produces: exposes the already-authenticated call invoker with no additional client interceptor, since
/// the session cookie travels automatically with every request over the channel's own
/// <see cref="System.Net.CookieContainer"/>-backed transport.
/// </summary>
/// <remarks>
/// Excluded from coverage with <see cref="SessionRouterConnector"/>: constructing one requires a live
/// <see cref="GrpcChannel"/>, which is the hop the connector seam exists to avoid in tests.
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class SessionRouterChannelProvider : IRouterChannelProvider, IDisposable
{
    private readonly GrpcChannel _channel;

    public SessionRouterChannelProvider(GrpcChannel channel, string serverAddress)
    {
        _channel = channel;
        ServerAddress = serverAddress;
        CallInvoker = channel.CreateCallInvoker();
    }

    /// <inheritdoc/>
    public CallInvoker CallInvoker { get; }

    /// <inheritdoc/>
    public string ServerAddress { get; }

    /// <summary>Disposes the underlying channel, closing its connection and releasing the session cookie's handler.</summary>
    public void Dispose()
    {
        _channel.Dispose();
    }
}
