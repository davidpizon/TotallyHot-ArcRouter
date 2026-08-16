using TotallyHot.ArcRouter.Gui.Telemetry;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Benchmark Data panel's "Local Voter Model" section.
/// Wraps <see cref="LlmRouterModelAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) with the same "singleton + Changed event + best-effort,
/// reachability-tolerant" shape as <see cref="BenchmarkDataStore"/>, so the UI survives tab switches and
/// degrades gracefully when the proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class LlmRouterModelStore : IDisposable
{
    private readonly ILlmRouterModelAdminClient _client;
    private readonly IDisposable? _ownedClient;
    private readonly ILogger<LlmRouterModelStore>? _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmRouterModelStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">The proxy's TLS gRPC endpoint; defaults to <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.</param>
    public LlmRouterModelStore(
        ILogger<LlmRouterModelStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        _logger = logger;
        var client = new LlmRouterModelAdminClient(serverAddress);
        _client = client;
        _ownedClient = client;
        ServerAddress = serverAddress;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmRouterModelStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    public LlmRouterModelStore(ILlmRouterModelAdminClient client, ILogger<LlmRouterModelStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedClient = null;
        _logger = logger;
    }

    /// <summary>
    /// The proxy endpoint this store's client talks to, so the unreachable state can name the address it
    /// actually failed to reach rather than assuming the default. <see langword="null"/> when constructed
    /// over a caller-supplied client, whose endpoint this store has no way to know.
    /// </summary>
    public string? ServerAddress { get; }

    /// <summary>The active model's last-known status, or <see langword="null"/> before the first load.</summary>
    public LlmRouterModelStatusInfo? Status { get; private set; }

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load or mutation reached the proxy.</summary>
    /// <remarks>Same connectivity-only meaning as <see cref="BenchmarkDataStore.IsReachable"/>.</remarks>
    public bool IsReachable { get; private set; }

    /// <summary>The message from the last failure to reach the proxy, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether a sync is currently running, so the UI can disable the button and show progress.</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>
    /// Every file's live progress during a sync, keyed by file name and refreshed as events stream in.
    /// Cleared at the start of each sync. Empty outside of a running sync.
    /// </summary>
    public IReadOnlyDictionary<string, LlmRouterModelSyncProgressInfo> SyncProgress => _syncProgress;

    private Dictionary<string, LlmRouterModelSyncProgressInfo> _syncProgress = [];

    /// <summary>Raised after any of the above change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the active model's cached status. Failures are swallowed and surfaced via
    /// <see cref="IsReachable"/>/<see cref="LastError"/> rather than thrown, so the tab renders an error
    /// state instead of crashing when the proxy isn't running.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Status = await _client.GetStatusAsync(cancellationToken);
            IsReachable = true;
            LastError = null;
        }
        catch (LlmRouterModelAdminException ex)
        {
            IsReachable = !ex.IsUnavailable;
            LastError = ex.Message;
            _logger?.LogWarning(ex, "Failed to load the llm_router model status from the router.");
        }
        finally
        {
            IsLoaded = true;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Switches the active model to <paramref name="baseUrl"/> and publishes the refreshed (now-unsynced-
    /// until-updated) status. Rethrows on failure - the panel has to render the rejection inline - the
    /// same split <see cref="SyncAsync"/> and <see cref="BenchmarkDataStore.RecheckAsync"/> use.
    /// </summary>
    /// <exception cref="LlmRouterModelAdminException">The switch was rejected or the router is unreachable.</exception>
    public async Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            Status = await _client.SetBaseUrlAsync(baseUrl, cancellationToken);
        }
        catch (LlmRouterModelAdminException ex)
        {
            RecordFailure(ex);
            throw;
        }

        IsReachable = true;
        IsLoaded = true;
        LastError = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Downloads and checksum-verifies (where possible) every file of the active model, publishing
    /// per-file progress into <see cref="SyncProgress"/> as it streams in and the final status once every
    /// file has been attempted. <see cref="IsSyncing"/> is true for the duration.
    /// </summary>
    /// <exception cref="LlmRouterModelAdminException">The sync could not be started or the router is unreachable.</exception>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        IsSyncing = true;
        _syncProgress = [];
        Changed?.Invoke();

        try
        {
            await foreach (var syncEvent in _client.SyncAsync(cancellationToken))
            {
                if (syncEvent.Progress is { } progress)
                {
                    _syncProgress[progress.FileName] = progress;
                }
                else if (syncEvent.FinalStatus is { } finalStatus)
                {
                    Status = finalStatus;
                }

                Changed?.Invoke();
            }

            IsReachable = true;
            IsLoaded = true;
            LastError = null;
        }
        catch (LlmRouterModelAdminException ex)
        {
            RecordFailure(ex);
            throw;
        }
        finally
        {
            // In a finally so a failed sync re-enables the button rather than leaving it stuck disabled.
            // The exception still propagates for the panel to render.
            IsSyncing = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Reflects a failed mutation in the store's state before the caller rethrows, so
    /// <see cref="IsReachable"/> keeps its documented meaning after a mutation and not only after a load.
    /// </summary>
    private void RecordFailure(LlmRouterModelAdminException ex)
    {
        if (!ex.IsUnavailable)
        {
            return;
        }

        IsReachable = false;
        LastError = ex.Message;
        _logger?.LogWarning(ex, "The router became unreachable during a llm_router model operation.");
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose() => _ownedClient?.Dispose();
}
