using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Benchmark Data panel. Wraps
/// <see cref="BenchmarkDataAdminClient"/> (the tested, platform-agnostic logic in TotallyHot.ArcRouter.Gui.Telemetry)
/// with the same "singleton + Changed event + best-effort, reachability-tolerant" shape as
/// <see cref="PriceSourceStore"/>, so the UI survives tab switches and degrades gracefully when the
/// proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class BenchmarkDataStore : IDisposable
{
    private readonly IBenchmarkDataAdminClient _client;
    private readonly ILogger<BenchmarkDataStore>? _logger;
    private readonly IDisposable? _ownedClient;

    private Dictionary<string, BenchmarkSyncProgressInfo> _syncProgress = [];

    /// <summary>Initializes a new instance of the <see cref="BenchmarkDataStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public BenchmarkDataStore(
        ILogger<BenchmarkDataStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        _logger = logger;
        var client = new BenchmarkDataAdminClient(serverAddress);
        _client = client;
        _ownedClient = client;
        ServerAddress = serverAddress;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BenchmarkDataStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    public BenchmarkDataStore(IBenchmarkDataAdminClient client, ILogger<BenchmarkDataStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownedClient = null;
        _logger = logger;
    }

    /// <summary>
    /// The proxy endpoint this store's client talks to, so the unreachable state can name the address it
    /// actually failed to reach rather than assuming the default. <see langword="null"/> when constructed
    /// over a caller-supplied client, whose endpoint this store has no way to know - the panel falls back
    /// to a generic message there instead of naming an endpoint that may be wrong.
    /// </summary>
    public string? ServerAddress { get; }

    /// <summary>The corpus's last-known freshness status, or <see langword="null"/> before the first load.</summary>
    public BenchmarkDataStatusInfo? Status { get; private set; }

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load or mutation reached the proxy.</summary>
    /// <remarks>Same connectivity-only meaning as <see cref="PriceSourceStore.IsReachable"/>.</remarks>
    public bool IsReachable { get; private set; }

    /// <summary>The message from the last failure to reach the proxy, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether a sync is currently running, so the UI can disable the button and show progress.</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>
    /// Every file's live progress during a sync, keyed by file name and refreshed as events stream in.
    /// Cleared at the start of each sync. Empty outside of a running sync.
    /// </summary>
    public IReadOnlyDictionary<string, BenchmarkSyncProgressInfo> SyncProgress => _syncProgress;

    /// <summary>
    /// The current sync's up-front plan - the stale files it will download and their combined size -
    /// published as the first event on the stream. <see langword="null"/> before that first event
    /// arrives (including outside of a running sync) or if the sync failed before it was sent.
    /// </summary>
    public BenchmarkSyncPlanInfo? SyncPlan { get; private set; }

    /// <summary>
    /// The name of the file the most recent progress event was about, i.e. the file currently being
    /// downloaded, verified, or imported. The server processes files strictly sequentially, so the
    /// latest event's file is always the one presently in flight.
    /// </summary>
    public string? CurrentFileName { get; private set; }

    /// <summary>
    /// The combined bytes transferred so far across every file in <see cref="SyncPlan"/>: a file that
    /// has not started counts 0, a file mid-download counts its <see cref="BenchmarkSyncProgressInfo.BytesTransferred"/>
    /// (capped at its planned size), and a file that has reached verifying, importing, or completed
    /// counts its full planned size regardless of the last reported byte count.
    /// </summary>
    public long CumulativeBytesTransferred => SyncPlan is null
        ? 0
        : SyncPlan.Files.Sum(file =>
        {
            if (!_syncProgress.TryGetValue(key: file.FileName, value: out var progress)) return 0L;

            return progress.Stage switch
            {
                BenchmarkSyncStageInfo.Verifying or BenchmarkSyncStageInfo.Importing or BenchmarkSyncStageInfo.Completed
                    => file.SizeBytes,
                _ => Math.Min(val1: progress.BytesTransferred ?? 0, val2: file.SizeBytes)
            };
        });

    /// <summary>The combined planned size of every file in <see cref="SyncPlan"/>, or 0 before the plan arrives.</summary>
    public long CumulativeTotalBytes => SyncPlan?.TotalBytes ?? 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        _ownedClient?.Dispose();
    }

    /// <summary>Raised after any of the above change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the corpus's cached status. Failures are swallowed and surfaced via
    /// <see cref="IsReachable"/>/<see cref="LastError"/> rather than thrown, so the tab renders an error
    /// state instead of crashing when the proxy isn't running.
    /// </summary>
    /// <remarks>
    /// Only a connectivity failure clears <see cref="IsReachable"/>, the same split
    /// <see cref="RecordFailure"/> applies to mutations: a status call the router *answered* with a
    /// rejection has to keep the panel's normal layout and show the rejection, because collapsing it into
    /// the "Router unreachable" state both misstates the cause and hides the message that explains it.
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Status = await _client.GetStatusAsync(cancellationToken);
            IsReachable = true;
            LastError = null;
        }
        catch (BenchmarkDataAdminException ex)
        {
            IsReachable = !ex.IsUnavailable;
            LastError = ex.Message;
            _logger?.LogWarning(exception: ex, message: "Failed to load the benchmark data status from the router.");
        }
        finally
        {
            IsLoaded = true;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Re-probes Hugging Face and publishes the recomputed status. Unlike <see cref="LoadAsync"/>, this
    /// rethrows: a recheck failing means "the thing you just asked for did not happen", which the panel
    /// has to be told inline, same split as <see cref="PriceSourceStore.SetEnabledAsync"/>.
    /// </summary>
    /// <exception cref="BenchmarkDataAdminException">The recheck failed or the router is unreachable.</exception>
    public async Task RecheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Status = await _client.RecheckAsync(cancellationToken);
        }
        catch (BenchmarkDataAdminException ex)
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
    /// Downloads, verifies, and imports every corpus file, publishing per-file progress into
    /// <see cref="SyncProgress"/> as it streams in and the final status once every file has been
    /// attempted. <see cref="IsSyncing"/> is true for the duration.
    /// </summary>
    /// <exception cref="BenchmarkDataAdminException">The sync could not be started or the router is unreachable.</exception>
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
                    // Certain stages (Failed, Verifying) often omit BytesTransferred/TotalBytes; carry the
                    // prior non-null values forward so a file's cumulative progress cannot regress to 0
                    // just because the latest event didn't repeat them.
                    if (_syncProgress.TryGetValue(key: progress.FileName, value: out var previous))
                        progress = progress with
                        {
                            BytesTransferred = progress.BytesTransferred ?? previous.BytesTransferred,
                            TotalBytes = progress.TotalBytes ?? previous.TotalBytes
                        };

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
        catch (BenchmarkDataAdminException ex)
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
    /// <remarks>
    /// Only a connectivity failure moves <see cref="IsReachable"/>. A rejection reached the router and is
    /// the panel's inline error to render; treating it as unreachable would replace the whole panel with a
    /// "router down" state that is both wrong and hides the actual message.
    /// </remarks>
    private void RecordFailure(BenchmarkDataAdminException ex)
    {
        if (!ex.IsUnavailable) return;

        IsReachable = false;
        LastError = ex.Message;
        _logger?.LogWarning(exception: ex, message: "The router became unreachable during a benchmark-data operation.");
        Changed?.Invoke();
    }
}