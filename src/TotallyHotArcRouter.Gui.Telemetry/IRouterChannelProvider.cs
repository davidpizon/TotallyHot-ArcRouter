using Grpc.Core;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Supplies the one shared, authenticated <see cref="Grpc.Core.CallInvoker"/> every admin client and
/// <c>TotallyHot.ArcRouter.Gui.Services.LiveDataStore</c>-style store talks through, replacing the
/// ~15 independent per-client <see cref="Grpc.Net.Client.GrpcChannel"/>s the web GUI migration plan's Phase
/// P5a found (one per admin client, each opening its own TCP+TLS connection to the same proxy process).
/// </summary>
/// <remarks>
/// Introduced so the stores that construct admin clients - which move into the browser-targeted
/// <c>TotallyHot.ArcRouter.Gui.Components</c> library in Phase P5b - never call
/// <see cref="TelemetryChannelFactory.Create"/> themselves: real TCP sockets and
/// <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/> chain validation don't
/// exist in a browser sandbox, so that channel-construction code must stay behind this interface, native-
/// only, where only a composition root (<c>MauiProgram</c> today, later the WASM host's own
/// <c>Program.cs</c> and the tray) ever constructs an implementation. A store only ever sees
/// <see cref="CallInvoker"/> and <see cref="ServerAddress"/>.
/// </remarks>
public interface IRouterChannelProvider
{
    /// <summary>
    /// Gets the shared, authenticated call invoker every admin client is constructed over. Callers never
    /// dispose this themselves - the provider implementation owns the underlying channel's lifetime.
    /// </summary>
    CallInvoker CallInvoker { get; }

    /// <summary>
    /// Gets the proxy endpoint <see cref="CallInvoker"/> talks to, so a store can report which address it
    /// actually failed to reach in its unreachable state, the same way each store's own
    /// <c>serverAddress</c> constructor parameter did before this phase.
    /// </summary>
    string ServerAddress { get; }
}
