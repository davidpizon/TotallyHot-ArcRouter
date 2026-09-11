using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the System Settings window's "Software Update" section. Wraps
/// <see cref="UpdateAdminClient"/> (status/check/audit-notify) and <see cref="MsiUpdateApplier"/>
/// (download/verify/launch) - both the tested, platform-agnostic logic in TotallyHot.ArcRouter.Gui.Telemetry
/// - in the shared <see cref="AdminStoreBase{TClient}"/> shape, so the UI survives modal close/reopen
/// and degrades gracefully when the proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class UpdateStore : AdminStoreBase<IUpdateAdminClient>
{
    private readonly IMsiUpdateApplier _applier;
    private readonly Action _exitApplication;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateStore"/> class, creating and owning both a client
    /// to <paramref name="serverAddress"/> and the <see cref="HttpClient"/> its installer applier downloads
    /// through.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public UpdateStore(
        ILogger<UpdateStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(client: new UpdateAdminClient(serverAddress), logger: logger, ownsClient: true)
    {
        var httpClient = Own(new HttpClient());
        _applier = new MsiUpdateApplier(httpClient: httpClient, logger: NullLogger<MsiUpdateApplier>.Instance);

        _exitApplication = () => Environment.Exit(0);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateStore"/> class over caller-supplied
    /// dependencies. The seam tests use to drive the store without a live proxy, a real download, or a
    /// real process exit; the caller owns the client's lifetime.
    /// </summary>
    /// <param name="client">Reads status and sends the apply-starting audit notification.</param>
    /// <param name="applier">Downloads, verifies, and launches the installer.</param>
    /// <param name="exitApplication">
    /// Invoked immediately after a successful apply launch - production wires this to actually terminate
    /// the process (<see cref="Environment.Exit(int)"/>), since this process cannot hold its own files
    /// locked while the MSI replaces <c>...\Gui\</c>. Defaults to a no-op so a test can assert it was
    /// called without ending the test process.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public UpdateStore(IUpdateAdminClient client, IMsiUpdateApplier applier, Action? exitApplication = null,
        ILogger<UpdateStore>? logger = null)
        : base(client: client, logger: logger)
    {
        ArgumentNullException.ThrowIfNull(applier);
        _applier = applier;
        _exitApplication = exitApplication ?? (() => { });
    }

    /// <summary>The last-loaded (or freshly checked) update status, or <see langword="null"/> before the first load.</summary>
    public UpdateStatusInfo? Status { get; private set; }

    /// <summary>Whether a check or apply is currently in flight, so the UI can disable buttons.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>The outcome of the most recent apply attempt, or <see langword="null"/> before one has run.</summary>
    public MsiApplyResult? LastApplyOutcome { get; private set; }

    /// <summary>
    /// Loads the last-known update status. Failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(
            async ct => Status = await Client.GetStatusAsync(ct).ConfigureAwait(false),
            "load the update status",
            cancellationToken);
    }

    /// <summary>Forces an immediate re-check - the "Check Now" button.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    public Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        return RunBusyAsync(
            async ct => Status = await Client.CheckNowAsync(ct).ConfigureAwait(false),
            "check for updates",
            cancellationToken);
    }

    /// <summary>
    /// Applies the currently-known-available update - the "Apply Update" button, which the panel is
    /// expected to gate behind its own confirmation dialog before calling this (applying downloads and
    /// installs an MSI, which requires administrator approval and restarts the application). Notifies the
    /// Router first (best-effort audit log, never blocking), then downloads/verifies/launches the
    /// installer via <see cref="IMsiUpdateApplier"/>. On a successful launch, invokes the exit callback
    /// supplied at construction so this process releases its own files before the MSI tries to replace
    /// them.
    /// </summary>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <exception cref="InvalidOperationException">
    /// No update is currently known available (call <see cref="LoadAsync"/>/
    /// <see cref="CheckNowAsync"/> first).
    /// </exception>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (Status is not
            { UpdateAvailable: true, AssetDownloadUrl: { } assetDownloadUrl, AssetSha256: { } assetSha256 } status)
            throw new InvalidOperationException(
                "No verified update is currently known available. Call LoadAsync/CheckNowAsync first.");

        IsBusy = true;
        NotifyChanged();

        try
        {
            await TryNotifyRouterAsync(latestVersion: status.LatestVersion, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            LastApplyOutcome = await _applier.ApplyAsync(assetDownloadUrl: assetDownloadUrl, assetSha256: assetSha256,
                latestVersion: status.LatestVersion, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (LastApplyOutcome.Succeeded) _exitApplication();
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Best-effort audit notification to the Router - a failure here (e.g. the router is unreachable, or
    /// already mid-shutdown) never blocks the apply, since the GUI already has everything it needs from
    /// its own cached status.
    /// </summary>
    private async Task TryNotifyRouterAsync(string latestVersion, CancellationToken cancellationToken)
    {
        try
        {
            await Client.NotifyApplyStartingAsync(version: latestVersion, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GrpcAdminException ex)
        {
            Logger?.LogWarning(exception: ex,
                message: "Could not notify the router that an apply is starting; proceeding anyway.");
        }
    }

    /// <summary>
    /// Runs one guarded operation with <see cref="IsBusy"/> held for its duration, so the panel's buttons
    /// re-enable even when it fails.
    /// </summary>
    /// <param name="operation">The status-refreshing call to run.</param>
    /// <param name="description">A short lower-case phrase naming the operation for the failure log.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    private async Task RunBusyAsync(Func<CancellationToken, Task> operation, string description,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        NotifyChanged();

        // Clearing IsBusy here, right before LoadGuardedAsync's own completion notification, means that
        // notification also carries "the buttons can re-enable" - instead of a third notification carrying
        // an intermediate "finished but still busy" state that no subscriber should ever see.
        await LoadGuardedAsync(operation: operation, description: description,
            cancellationToken: cancellationToken, beforeNotify: () => IsBusy = false);
    }
}
