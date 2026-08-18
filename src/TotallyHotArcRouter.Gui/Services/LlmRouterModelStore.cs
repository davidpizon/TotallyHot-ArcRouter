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
    /// Cleared at the start of each sync and on a successful <see cref="SetBaseUrlAsync"/>. Not cleared
    /// when a sync finishes - the terminal (including Failed) events remain so the panel can keep
    /// rendering per-file errors after <see cref="IsSyncing"/> goes false.
    /// </summary>
    public IReadOnlyDictionary<string, LlmRouterModelSyncProgressInfo> SyncProgress => _syncProgress;

    private Dictionary<string, LlmRouterModelSyncProgressInfo> _syncProgress = [];

    /// <summary>
    /// The current sync's plan - which files are stale and how many bytes the run will transfer - or
    /// <see langword="null"/> before the plan event arrives (or when no sync is running). Cleared at the
    /// start of each <see cref="SyncAsync"/> call.
    /// </summary>
    public LlmRouterModelSyncPlanInfo? SyncPlan { get; private set; }

    /// <summary>
    /// The file whose most recent progress event is non-terminal, driving the current-file progress bar's
    /// label. <see langword="null"/> before the first progress event of a sync arrives.
    /// </summary>
    public string? CurrentFileName { get; private set; }

    /// <summary>
    /// The combined bytes transferred so far across every planned file, derived from <see cref="SyncPlan"/>
    /// and <see cref="SyncProgress"/> rather than accumulated, so it stays correct under out-of-order or
    /// repeated events: each planned file contributes its full size once its stage reaches
    /// <see cref="LlmRouterModelSyncStageInfo.Verifying"/> or later, otherwise the lesser of its reported
    /// bytes transferred and its planned size. 0 before the plan arrives.
    /// </summary>
    public long CumulativeBytesTransferred => SyncPlan is null
        ? 0
        : SyncPlan.Files.Sum(file =>
        {
            if (!_syncProgress.TryGetValue(file.FileName, out var progress))
            {
                return 0L;
            }

            return progress.Stage switch
            {
                LlmRouterModelSyncStageInfo.Verifying or LlmRouterModelSyncStageInfo.Completed => file.SizeBytes,
                _ => Math.Min(progress.BytesTransferred ?? 0, file.SizeBytes),
            };
        });

    /// <summary>The combined planned size of every file in <see cref="SyncPlan"/>, or 0 before the plan arrives.</summary>
    public long CumulativeTotalBytes => SyncPlan?.TotalBytes ?? 0;

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

        // The new model's files share the old model's file names (genai_config.json, model.onnx, ...),
        // so a leftover progress entry from the previous model would otherwise be misread as this one's.
        _syncProgress = [];
        SyncPlan = null;
        CurrentFileName = null;
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
        SyncPlan = null;
        CurrentFileName = null;
        Changed?.Invoke();

        try
        {
            await foreach (var syncEvent in _client.SyncAsync(cancellationToken))
            {
                if (syncEvent.Plan is { } plan)
                {
                    SyncPlan = plan;
                }
                else if (syncEvent.Progress is { } progress)
                {
                    // A terminal stage (Failed, Verifying) often omits BytesTransferred/TotalBytes; carry
                    // the prior non-null values forward so a file's cumulative progress cannot regress to
                    // 0 just because the latest event didn't repeat them.
                    if (_syncProgress.TryGetValue(progress.FileName, out var previous))
                    {
                        progress = progress with
                        {
                            BytesTransferred = progress.BytesTransferred ?? previous.BytesTransferred,
                            TotalBytes = progress.TotalBytes ?? previous.TotalBytes,
                        };
                    }

                    _syncProgress[progress.FileName] = progress;
                    CurrentFileName = progress.FileName;
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
