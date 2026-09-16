using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Tray;

/// <summary>
/// Backs the tray context menu's "Install update" item: checks the router's last-known update status via
/// <see cref="IUpdateAdminClient"/>, then downloads/verifies/launches the installer via
/// <see cref="IMsiUpdateApplier"/> when one is available - the same apply flow
/// <c>TotallyHot.ArcRouter.Gui.Services.UpdateStore</c> drives for the browser GUI's System Settings
/// window, reduced to what a context-menu item needs rather than a full view-model
/// (<c>AdminStoreBase</c>'s busy/error/reachability tracking is a Blazor-facing concern this menu item
/// doesn't have - see <see cref="RoutingGateMonitor"/>'s remarks on why this library doesn't reference
/// <c>TotallyHotArcRouter.Gui.Components</c>). Reuses <see cref="IMsiUpdateApplier"/> itself unchanged, per
/// the plan's "Reuse (do not rebuild)" list.
/// </summary>
public sealed class TrayUpdateCoordinator
{
    private readonly IMsiUpdateApplier _applier;
    private readonly IUpdateAdminClient _client;
    private readonly Action _exitApplication;

    /// <summary>Initializes a new instance of the <see cref="TrayUpdateCoordinator"/> class.</summary>
    /// <param name="client">Reads the update status and sends the apply-starting audit notification.</param>
    /// <param name="applier">Downloads, verifies, and launches the installer.</param>
    /// <param name="exitApplication">
    /// Invoked immediately after a successful apply launch - production wires this to actually terminate
    /// the tray process, since the installer replaces this process's own files while it may still be
    /// running. Defaults to <see cref="Environment.Exit(int)"/>; a test supplies a no-op.
    /// </param>
    public TrayUpdateCoordinator(IUpdateAdminClient client, IMsiUpdateApplier applier, Action? exitApplication = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(applier);

        _client = client;
        _applier = applier;
        _exitApplication = exitApplication ?? (() => Environment.Exit(0));
    }

    /// <summary>The last-loaded (or freshly checked) update status, or <see langword="null"/> before the first check.</summary>
    public UpdateStatusInfo? Status { get; private set; }

    /// <summary>
    /// Forces an immediate re-check against the router - what the tray calls right before offering
    /// "Install update" in its context menu, so the menu item's presence always reflects the router's
    /// current answer rather than a stale cached one.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    public async Task<UpdateStatusInfo> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        Status = await _client.CheckNowAsync(cancellationToken).ConfigureAwait(false);
        return Status;
    }

    /// <summary>
    /// Applies the currently-known-available update: notifies the router first (best-effort audit log,
    /// never blocking), then downloads/verifies/launches the installer via <see cref="IMsiUpdateApplier"/>.
    /// On a successful launch, invokes the exit callback supplied at construction so this process releases
    /// its own files before the MSI tries to replace them.
    /// </summary>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <exception cref="InvalidOperationException">
    /// No update is currently known available (call <see cref="CheckNowAsync"/> first).
    /// </exception>
    public async Task<MsiApplyResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (Status is not
            { UpdateAvailable: true, AssetDownloadUrl: { } assetDownloadUrl, AssetSha256: { } assetSha256 } status)
            throw new InvalidOperationException(
                "No verified update is currently known available. Call CheckNowAsync first.");

        await TryNotifyRouterAsync(latestVersion: status.LatestVersion, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var result = await _applier.ApplyAsync(assetDownloadUrl: assetDownloadUrl, assetSha256: assetSha256,
            latestVersion: status.LatestVersion, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) _exitApplication();

        return result;
    }

    /// <summary>
    /// Best-effort audit notification to the router - a failure here (e.g. the router is unreachable, or
    /// already mid-shutdown) never blocks the apply, since the tray already has everything it needs from
    /// its own cached <see cref="Status"/>.
    /// </summary>
    private async Task TryNotifyRouterAsync(string latestVersion, CancellationToken cancellationToken)
    {
        try
        {
            await _client.NotifyApplyStartingAsync(version: latestVersion, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GrpcAdminException)
        {
            // Swallowed deliberately - see this method's remarks.
        }
    }
}
