using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Routing Mode panel. Wraps
/// <see cref="RoutingModeAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) in the shared <see cref="AdminStoreBase{TClient}"/> shape, so the
/// UI survives tab switches and degrades gracefully when the proxy isn't running. Registered in
/// <c>MauiProgram</c>.
/// </summary>
public sealed class RoutingModeStore : AdminStoreBase<IRoutingModeAdminClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingModeStore"/> class, over the shared
    /// <see cref="IRouterChannelProvider"/> every admin client and store talks through (web GUI
    /// migration plan Phase P5a) - see <see cref="IRouterChannelProvider"/>'s remarks.
    /// </summary>
    /// <param name="channelProvider">Supplies the shared call invoker this store's client is constructed over.</param>
    /// <param name="logger">Optional logger.</param>
    public RoutingModeStore(
        IRouterChannelProvider channelProvider,
        ILogger<RoutingModeStore>? logger = null)
        : base(client: new RoutingModeAdminClient(channelProvider.CallInvoker), logger: logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingModeStore"/> class over a caller-supplied client.
    /// The seam tests use to drive the store without a live proxy; the caller owns the client's lifetime.
    /// </summary>
    /// <param name="client">The admin client to read through.</param>
    /// <param name="logger">Optional logger.</param>
    public RoutingModeStore(IRoutingModeAdminClient client, ILogger<RoutingModeStore>? logger = null)
        : base(client: client, logger: logger)
    {
    }

    /// <summary>The routing mode as of the last successful load, or <see langword="null"/> before the first one.</summary>
    public RoutingMode? Mode { get; private set; }

    /// <summary>
    /// Loads the routing mode. Connection failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>
    /// rather than thrown, so the tab renders an "unreachable" state instead of crashing when the proxy
    /// isn't running.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct => Mode = await Client.GetAsync(ct),
            "read the routing mode",
            cancellationToken);
    }
}
