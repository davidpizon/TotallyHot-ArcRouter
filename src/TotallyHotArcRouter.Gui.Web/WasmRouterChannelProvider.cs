using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.AspNetCore.Components;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Web;

/// <summary>
/// The browser <see cref="IRouterChannelProvider"/> implementation (web GUI migration plan Phase P6):
/// a gRPC-Web channel to the same origin the WASM app was served from. A browser cannot speak trailers-
/// based HTTP/2 gRPC directly, so calls go out through <see cref="GrpcWebHandler"/>'s HTTP/1.1-or-
/// HTTP/2-safe framing to the router's web-port gRPC-Web listener (Phase P2) instead of a raw
/// <see cref="TelemetryChannelFactory"/> channel.
/// </summary>
/// <remarks>
/// Same-origin by construction - <see cref="ServerAddress"/> is <see cref="NavigationManager.BaseUri"/>,
/// never a configurable address. The browser's own same-origin cookie policy is what carries the
/// ADR-0012 session cookie on every call with no explicit credential handling needed here.
/// </remarks>
public sealed class WasmRouterChannelProvider : IRouterChannelProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WasmRouterChannelProvider"/> class, opening a
    /// gRPC-Web channel to <paramref name="navigation"/>'s current base URI.
    /// </summary>
    /// <param name="navigation">Supplies the origin the WASM app was served from.</param>
    public WasmRouterChannelProvider(NavigationManager navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        ServerAddress = navigation.BaseUri;
        var httpClient = new HttpClient(new GrpcWebHandler(mode: GrpcWebMode.GrpcWeb, innerHandler: new HttpClientHandler()))
        {
            BaseAddress = new Uri(ServerAddress)
        };
        var channel = GrpcChannel.ForAddress(address: ServerAddress, channelOptions: new GrpcChannelOptions
        {
            HttpClient = httpClient
        });
        CallInvoker = channel.CreateCallInvoker();
    }

    /// <inheritdoc/>
    public CallInvoker CallInvoker { get; }

    /// <inheritdoc/>
    public string ServerAddress { get; }
}
