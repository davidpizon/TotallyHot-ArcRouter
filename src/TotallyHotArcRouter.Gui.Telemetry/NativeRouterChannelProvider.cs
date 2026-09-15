using Grpc.Core;
using Grpc.Net.Client;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The native (real TCP+TLS socket) <see cref="IRouterChannelProvider"/> implementation: opens exactly one
/// <see cref="GrpcChannel"/> to the proxy via <see cref="TelemetryChannelFactory"/> and shares its
/// authenticated <see cref="CallInvoker"/> across every admin client and store. Constructed once by a
/// native composition root (<c>MauiProgram</c> today; later the tray) and registered as a singleton, so its
/// <see cref="Dispose"/> is what actually closes the shared connection at shutdown.
/// </summary>
public sealed class NativeRouterChannelProvider : IRouterChannelProvider, IDisposable
{
    private readonly GrpcChannel _channel;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeRouterChannelProvider"/> class, opening and
    /// owning a channel to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="serverAddress">The proxy's TLS gRPC endpoint; defaults to <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.</param>
    public NativeRouterChannelProvider(string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);

        ServerAddress = serverAddress;
        _channel = TelemetryChannelFactory.Create(serverAddress);
        CallInvoker = TelemetryChannelFactory.Authenticated(_channel);
    }

    /// <inheritdoc/>
    public CallInvoker CallInvoker { get; }

    /// <inheritdoc/>
    public string ServerAddress { get; }

    /// <summary>Disposes the shared channel, closing the underlying connection.</summary>
    public void Dispose()
    {
        _channel.Dispose();
    }
}
