using TotallyHot.ArcRouter.Gui.Telemetry;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the System Settings window's "Software Update" section. Wraps
/// <see cref="UpdateAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) with the same "singleton + Changed event + best-effort,
/// reachability-tolerant" shape as <see cref="LlmRouterModelStore"/>, so the UI survives tab switches and
/// degrades gracefully when the proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class UpdateStore : IDisposable
{
    private readonly IUpdateAdminClient _client;
    private readonly IDisposable? _ownedClient;
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
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateStore"/> class over a caller-supplied client.
    /// The seam tests use to drive the store without a live proxy; the caller owns the client's lifetime.
    /// </summary>
    public UpdateStore(IUpdateAdminClient client, ILogger<UpdateStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedClient = null;
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
    public ApplyUpdateInfo? LastApplyOutcome { get; private set; }

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
    /// expected to gate behind its own confirmation dialog before calling this (applying restarts the
    /// Router service). Rethrows so the panel can render the rejection inline, matching
    /// <see cref="LlmRouterModelStore.SetBaseUrlAsync"/>'s split.
    /// </summary>
    /// <exception cref="UpdateAdminException">The apply was rejected, failed, or the router is unreachable.</exception>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        Changed?.Invoke();

        try
        {
            LastApplyOutcome = await _client.ApplyAsync(cancellationToken).ConfigureAwait(false);
            IsReachable = true;
            LastError = null;
        }
        catch (UpdateAdminException ex)
        {
            RecordFailure(ex);
            throw;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
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
    public void Dispose() => _ownedClient?.Dispose();
}
