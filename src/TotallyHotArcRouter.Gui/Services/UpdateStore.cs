using TotallyHot.ArcRouter.Gui.Telemetry;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the System Settings window's "Software Update" section. Wraps
/// <see cref="UpdateAdminClient"/> (status/check/audit-notify) and <see cref="MsiUpdateApplier"/>
/// (download/verify/launch) - both the tested, platform-agnostic logic in TotallyHot.ArcRouter.Gui.Telemetry
/// - with the same "singleton + Changed event + best-effort, reachability-tolerant" shape as
/// <see cref="LlmRouterModelStore"/>, so the UI survives tab switches and degrades gracefully when the
/// proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class UpdateStore : IDisposable
{
    private readonly IUpdateAdminClient _client;
    private readonly IMsiUpdateApplier _applier;
    private readonly IDisposable? _ownedClient;
    private readonly HttpClient? _ownedHttpClient;
    private readonly Action _exitApplication;
    private readonly ILogger<UpdateStore>? _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdateStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">The proxy's TLS gRPC endpoint; defaults to <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.</param>
    public UpdateStore(
        ILogger<UpdateStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        _logger = logger;
        var client = new UpdateAdminClient(serverAddress);
        _client = client;
        _ownedClient = client;

        var httpClient = new HttpClient();
        _ownedHttpClient = httpClient;
        _applier = new MsiUpdateApplier(httpClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<MsiUpdateApplier>.Instance);

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
    public UpdateStore(IUpdateAdminClient client, IMsiUpdateApplier applier, Action? exitApplication = null, ILogger<UpdateStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(applier);
        _client = client;
        _applier = applier;
        _ownedClient = null;
        _ownedHttpClient = null;
        _exitApplication = exitApplication ?? (() => { });
        _logger = logger;
    }

    /// <summary>The last-loaded (or freshly checked) update status, or <see langword="null"/> before the first load.</summary>
    public UpdateStatusInfo? Status { get; private set; }

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load or mutation reached the proxy.</summary>
    public bool IsReachable { get; private set; }

    /// <summary>The message from the last failure to reach or use the proxy, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether a check or apply is currently in flight, so the UI can disable buttons.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>The outcome of the most recent apply attempt, or <see langword="null"/> before one has run.</summary>
    public MsiApplyResult? LastApplyOutcome { get; private set; }

    /// <summary>Raised after any of the above change.</summary>
    public event Action? Changed;

    /// <summary>Loads the last-known update status. Failures are swallowed and surfaced via <see cref="IsReachable"/>/<see cref="LastError"/>.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _client.GetStatusAsync(cancellationToken), "load the update status");

    /// <summary>Forces an immediate re-check - the "Check Now" button.</summary>
    public Task CheckNowAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _client.CheckNowAsync(cancellationToken), "check for updates");

    /// <summary>
    /// Applies the currently-known-available update - the "Apply Update" button, which the panel is
    /// expected to gate behind its own confirmation dialog before calling this (applying downloads and
    /// installs an MSI, which requires administrator approval and restarts the application). Notifies the
    /// Router first (best-effort audit log, never blocking), then downloads/verifies/launches the
    /// installer via <see cref="IMsiUpdateApplier"/>. On a successful launch, invokes the exit callback
    /// supplied at construction so this process releases its own files before the MSI tries to replace
    /// them.
    /// </summary>
    /// <exception cref="InvalidOperationException">No update is currently known available (call <see cref="LoadAsync"/>/<see cref="CheckNowAsync"/> first).</exception>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (Status is not { UpdateAvailable: true, AssetDownloadUrl: { } assetDownloadUrl, AssetSha256: { } assetSha256 } status)
        {
            throw new InvalidOperationException("No verified update is currently known available. Call LoadAsync/CheckNowAsync first.");
        }

        IsBusy = true;
        Changed?.Invoke();

        try
        {
            await TryNotifyRouterAsync(status.LatestVersion, cancellationToken).ConfigureAwait(false);

            LastApplyOutcome = await _applier.ApplyAsync(assetDownloadUrl, assetSha256, status.LatestVersion, cancellationToken).ConfigureAwait(false);
            if (LastApplyOutcome.Succeeded)
            {
                _exitApplication();
            }
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
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
            await _client.NotifyApplyStartingAsync(latestVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateAdminException ex)
        {
            _logger?.LogWarning(ex, "Could not notify the router that an apply is starting; proceeding anyway.");
        }
    }

    /// <summary>Runs one status-returning operation, updating the store's state and swallowing failures into <see cref="IsReachable"/>/<see cref="LastError"/>.</summary>
    private async Task RunAsync(Func<Task<UpdateStatusInfo>> operation, string action)
    {
        IsBusy = true;
        Changed?.Invoke();

        try
        {
            Status = await operation().ConfigureAwait(false);
            IsReachable = true;
            LastError = null;
        }
        catch (UpdateAdminException ex)
        {
            RecordFailure(ex);
            _logger?.LogWarning(ex, "Failed to {Action} from the router.", action);
        }
        finally
        {
            IsLoaded = true;
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Reflects a failed operation in the store's reachability state.</summary>
    private void RecordFailure(UpdateAdminException ex)
    {
        IsReachable = !ex.IsUnavailable;
        LastError = ex.Message;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ownedClient?.Dispose();
        _ownedHttpClient?.Dispose();
    }
}
