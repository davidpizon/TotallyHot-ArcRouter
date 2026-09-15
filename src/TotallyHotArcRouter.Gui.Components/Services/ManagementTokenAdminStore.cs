using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the System Settings window's "Copy MCP token / Regenerate" row (web GUI
/// migration plan Phase P9). Wraps <see cref="IManagementTokenAdminClient"/> in the shared
/// <see cref="AdminStoreBase{TClient}"/> shape, so the panel survives modal close/reopen and degrades
/// gracefully when the router isn't running - see <see cref="AdminStoreBase{TClient}"/>'s remarks for why
/// this, unlike <c>RoutingGateStore</c>, fits that base cleanly. Registered in <c>MauiProgram</c>/the WASM
/// host's composition root.
/// </summary>
public sealed class ManagementTokenAdminStore : AdminStoreBase<IManagementTokenAdminClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementTokenAdminStore"/> class over the shared
    /// <see cref="IRouterChannelProvider"/> (web GUI migration plan Phase P5a).
    /// </summary>
    /// <param name="channelProvider">
    /// Supplies the shared call invoker this store's client is constructed over - see
    /// <see cref="IRouterChannelProvider"/>'s remarks.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public ManagementTokenAdminStore(IRouterChannelProvider channelProvider,
        ILogger<ManagementTokenAdminStore>? logger = null)
        : base(client: new ManagementTokenAdminClient(channelProvider.CallInvoker), logger: logger, ownsClient: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementTokenAdminStore"/> class over caller-supplied
    /// dependencies. The seam tests use to drive the store without a live proxy; the caller owns the
    /// client's lifetime.
    /// </summary>
    /// <param name="client">Reads and rotates the shared management token.</param>
    /// <param name="logger">Optional logger.</param>
    public ManagementTokenAdminStore(IManagementTokenAdminClient client, ILogger<ManagementTokenAdminStore>? logger = null)
        : base(client: client, logger: logger)
    {
    }

    /// <summary>The last-loaded (or freshly regenerated) token, or <see langword="null"/> before the first load.</summary>
    public string? Token { get; private set; }

    /// <summary>
    /// Loads the router's current management token. Failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct => Token = await Client.GetTokenAsync(ct).ConfigureAwait(false),
            "load the management token",
            cancellationToken);
    }

    /// <summary>
    /// Mints and persists a fresh token - the confirmed "Regenerate" action, expected to already be gated
    /// behind the caller's own confirm dialog (invalidating every already-configured MCP client's saved
    /// token is not undoable). Rethrows on failure so the caller's confirm flow can report it inline, in
    /// addition to recording it in <see cref="AdminStoreBase{TClient}.LastError"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    public async Task RegenerateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Token = await Client.RegenerateAsync(cancellationToken).ConfigureAwait(false);
            RecordSuccess();
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a management token regenerate", recordRejectionMessage: true);
            throw;
        }

        NotifyChanged();
    }
}
