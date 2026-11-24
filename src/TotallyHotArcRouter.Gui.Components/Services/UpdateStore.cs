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
    /// <summary>
    /// The project's GitHub releases page, shown as a link when <see cref="SupportsApply"/> is
    /// <see langword="false"/> and an update is available - the operator's only path to actually getting
    /// it, since this host cannot launch the installer itself. Matches the repository
    /// <c>GitHubReleaseCheckClient</c> checks against by default.
    /// </summary>
    public const string ReleasesUrl = "https://github.com/davidpizon/TotallyHot-ArcRouter/releases/latest";

    private static readonly TimeSpan DefaultTrayClaimGrace = TimeSpan.FromSeconds(8);

    private readonly IMsiUpdateApplier _applier;
    private readonly Action _exitApplication;
    private readonly TimeSpan _trayClaimGrace;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateStore"/> class, over the shared
    /// <see cref="IRouterChannelProvider"/> (web GUI migration plan Phase P5a), and owning the
    /// <see cref="HttpClient"/> its installer applier downloads through.
    /// </summary>
    /// <param name="channelProvider">
    /// Supplies the shared call invoker this store's client is constructed over - see
    /// <see cref="IRouterChannelProvider"/>'s remarks.
    /// </param>
    /// <param name="supportsApply">See <see cref="SupportsApply"/>. Defaults to <see langword="true"/> -
    /// the native MAUI host, which can actually launch an MSI.</param>
    /// <param name="logger">Optional logger.</param>
    public UpdateStore(
        IRouterChannelProvider channelProvider,
        bool supportsApply = true,
        ILogger<UpdateStore>? logger = null)
        : base(client: new UpdateAdminClient(channelProvider.CallInvoker), logger: logger)
    {
        var httpClient = Own(new HttpClient());
        _applier = new MsiUpdateApplier(httpClient: httpClient, logger: NullLogger<MsiUpdateApplier>.Instance);

        _exitApplication = () => Environment.Exit(0);
        _trayClaimGrace = DefaultTrayClaimGrace;
        SupportsApply = supportsApply;
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
    /// <param name="supportsApply">See <see cref="SupportsApply"/>. Defaults to <see langword="true"/>.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="trayClaimGrace">
    /// How long <see cref="RequestHeaderApplyAsync"/> waits for the tray to claim the install before
    /// the header says the tray app has to be open. Defaults to eight seconds. Tests pass a few
    /// milliseconds.
    /// </param>
    public UpdateStore(IUpdateAdminClient client, IMsiUpdateApplier applier, Action? exitApplication = null,
        bool supportsApply = true, ILogger<UpdateStore>? logger = null, TimeSpan? trayClaimGrace = null)
        : base(client: client, logger: logger)
    {
        ArgumentNullException.ThrowIfNull(applier);
        _applier = applier;
        _exitApplication = exitApplication ?? (() => { });
        _trayClaimGrace = trayClaimGrace ?? DefaultTrayClaimGrace;
        SupportsApply = supportsApply;
    }

    /// <summary>
    /// Whether this host can actually launch the downloaded MSI - <see langword="true"/> for the native
    /// MAUI host, <see langword="false"/> for the WASM host (<c>TotallyHotArcRouter.Gui.Web</c>), which
    /// runs sandboxed inside a browser tab and has no filesystem or process-launch capability to do so
    /// (D11, web GUI migration plan). The panel is expected to hide its "Apply Update" action and show a
    /// link to <see cref="ReleasesUrl"/> instead when this is <see langword="false"/>; <see cref="ApplyAsync"/>
    /// itself does not check this flag; a WASM host that ignored it and called it anyway would still fail
    /// safely - <see cref="MsiUpdateApplier"/>'s file/process APIs simply throw
    /// <see cref="PlatformNotSupportedException"/> in the browser sandbox - but hiding the action is what
    /// actually prevents a confusing failure.
    /// </summary>
    public bool SupportsApply { get; }

    /// <summary>The last-loaded (or freshly checked) update status, or <see langword="null"/> before the first load.</summary>
    public UpdateStatusInfo? Status { get; private set; }

    /// <summary>Whether a check or apply is currently in flight, so the UI can disable buttons.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>The outcome of the most recent apply attempt, or <see langword="null"/> before one has run.</summary>
    public MsiApplyResult? LastApplyOutcome { get; private set; }

    /// <summary>
    /// Whether the header should read "Updating…" and stay disabled. True from the click until the tray
    /// claims the install. Cleared when the install is still waiting after <see cref="RequestHeaderApplyAsync"/>'s
    /// grace, so the operator can see that the tray has to be open.
    /// </summary>
    public bool InstallInProgress { get; private set; }

    /// <summary>
    /// Whether the pending install was still unclaimed after the grace period. The header then says the
    /// tray app has to be open to finish the install. The install stays recorded, so a tray that starts
    /// later can still claim it.
    /// </summary>
    public bool TrayMustBeOpen { get; private set; }

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
    /// Asks the router to remember one pending install for the tray. Does not download or launch anything
    /// in this process — the dashboard cannot. After a short wait, if the tray has not claimed the
    /// install, <see cref="TrayMustBeOpen"/> becomes true.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request and the wait.</param>
    public async Task RequestHeaderApplyAsync(CancellationToken cancellationToken = default)
    {
        InstallInProgress = true;
        TrayMustBeOpen = false;
        IsBusy = true;
        NotifyChanged();

        try
        {
            await Client.RequestApplyAsync(cancellationToken).ConfigureAwait(false);
            Status = await Client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(delay: _trayClaimGrace, cancellationToken: cancellationToken).ConfigureAwait(false);
            var status = await Client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            Status = status;
            if (status.ApplyPending)
            {
                InstallInProgress = false;
                TrayMustBeOpen = true;
            }
        }
        catch (GrpcAdminException ex)
        {
            InstallInProgress = false;
            TrayMustBeOpen = false;
            LastApplyOutcome = MsiApplyResult.Failure(ex.Message);
            Logger?.LogWarning(exception: ex, message: "Could not ask the tray to install the update.");
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
