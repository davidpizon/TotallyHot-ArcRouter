using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the System Settings window's "Copy MCP token / Regenerate" row (web GUI
/// migration plan Phase P9). Wraps <see cref="IManagementTokenAdminClient"/> in the shared
/// <see cref="AdminStoreBase{TClient}"/> shape, so the panel survives modal close/reopen and degrades
/// gracefully when the router isn't running. Registered in the WASM host's composition root.
/// </summary>
public sealed class ManagementTokenAdminStore : AdminStoreBase<IManagementTokenAdminClient>
{
    private readonly Func<CancellationToken, Task>? _reauthenticateAsync;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementTokenAdminStore"/> class over the shared
    /// <see cref="IRouterChannelProvider"/> (web GUI migration plan Phase P5a).
    /// </summary>
    /// <param name="channelProvider">
    /// Supplies the shared call invoker this store's client is constructed over - see
    /// <see cref="IRouterChannelProvider"/>'s remarks.
    /// </param>
    /// <param name="reauthenticateAsync">See the caller-supplied-dependencies constructor's remarks.</param>
    /// <param name="logger">Optional logger.</param>
    public ManagementTokenAdminStore(IRouterChannelProvider channelProvider,
        Func<CancellationToken, Task>? reauthenticateAsync = null,
        ILogger<ManagementTokenAdminStore>? logger = null)
        : base(client: new ManagementTokenAdminClient(channelProvider.CallInvoker), logger: logger)
    {
        _reauthenticateAsync = reauthenticateAsync;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagementTokenAdminStore"/> class over caller-supplied
    /// dependencies. The seam tests use to drive the store without a live proxy; the caller owns the
    /// client's lifetime.
    /// </summary>
    /// <param name="client">Reads and rotates the shared management token.</param>
    /// <param name="reauthenticateAsync">
    /// Invoked, best-effort, after a successful <see cref="RegenerateAsync"/> - re-establishes whatever
    /// credential this host's own management calls actually authenticate with, since rotation bumps
    /// <c>IManagementTokenProvider.Generation</c> and immediately invalidates every session ticket issued
    /// under the old generation (see <c>ManagementSessionTicketService</c>'s remarks), including the
    /// caller's own loopback session cookie - without this, the tab that just clicked Regenerate locks
    /// itself out of every further management call until a manual page reload. The browser-hosted WASM
    /// dashboard wires this to a fresh <c>POST /auth/session</c> (see
    /// <c>TotallyHotArcRouter.Gui.Web/Program.cs</c>'s own bootstrap call for the same request); a
    /// non-browser host with nothing session-cookie-shaped to refresh (the Tray's native-gRPC bearer
    /// token, which already gets its new value straight from <see cref="RegenerateAsync"/>'s own return)
    /// passes <see langword="null"/>. A failure here is swallowed the same way <c>Program.cs</c>'s own
    /// bootstrap call swallows one - this store's ordinary reachability state covers the user-facing
    /// fallback (the next load simply reports unreachable/unauthenticated).
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public ManagementTokenAdminStore(IManagementTokenAdminClient client,
        Func<CancellationToken, Task>? reauthenticateAsync = null,
        ILogger<ManagementTokenAdminStore>? logger = null)
        : base(client: client, logger: logger)
    {
        _reauthenticateAsync = reauthenticateAsync;
    }

    /// <summary>
    /// The last-fetched (or freshly regenerated) token, or <see langword="null"/> when none is held. Held only
    /// between a successful gated call and <see cref="ClearToken"/>: since ADR-0020 the router releases the
    /// token only against a passkey ceremony, so a token left sitting in memory would outlive the approval
    /// that earned it.
    /// </summary>
    public string? Token { get; private set; }

    /// <summary>
    /// Fetches the router's current management token. The router requires a single-use passkey
    /// authorization bound to <see cref="PasskeyOperations.GetManagementToken"/> (ADR-0020), so the caller
    /// runs the ceremony first and passes the result here. Failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>;
    /// a refusal (missing or spent authorization) leaves <see cref="Token"/> <see langword="null"/>.
    /// </summary>
    /// <param name="authorizationToken">The one-operation authorization from the passkey ceremony.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>Whether the token was fetched.</returns>
    public Task<bool> LoadAsync(string authorizationToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorizationToken);

        return LoadGuardedAsync(
            async ct => Token = await Client.GetTokenAsync(authorizationToken, ct).ConfigureAwait(false),
            "load the management token",
            cancellationToken,
            onFailure: () => Token = null);
    }

    /// <summary>Drops the held token so it does not outlive the passkey approval that fetched it.</summary>
    public void ClearToken()
    {
        if (Token is null) return;

        Token = null;
        NotifyChanged();
    }

    /// <summary>
    /// Mints and persists a fresh token - the confirmed "Regenerate" action, expected to already be gated
    /// behind the caller's own confirm dialog (invalidating every already-configured MCP client's saved
    /// token is not undoable) and then a passkey ceremony bound to
    /// <see cref="PasskeyOperations.RegenerateManagementToken"/> (ADR-0020). Rethrows on failure so the
    /// caller's confirm flow can report it inline, in addition to recording it in
    /// <see cref="AdminStoreBase{TClient}.LastError"/>.
    /// </summary>
    /// <param name="authorizationToken">The one-operation authorization from the passkey ceremony.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <exception cref="GrpcAdminException">The call failed, was not authorized, or the router is unreachable.</exception>
    public async Task RegenerateAsync(string authorizationToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorizationToken);

        try
        {
            Token = await Client.RegenerateAsync(authorizationToken, cancellationToken).ConfigureAwait(false);
            RecordSuccess();
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a management token regenerate", recordRejectionMessage: true);
            throw;
        }

        if (_reauthenticateAsync is not null)
        {
            try
            {
                await _reauthenticateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort, same as Program.cs's own session bootstrap: this store's ordinary
                // reachability state (the next load reporting unreachable/unauthenticated) is the
                // user-facing fallback, not a thrown exception from a Regenerate() the router itself
                // already confirmed succeeded.
            }
        }

        NotifyChanged();
    }
}
