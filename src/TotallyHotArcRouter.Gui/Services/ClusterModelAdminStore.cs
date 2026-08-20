using TotallyHot.ArcRouter.Gui.Telemetry;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Cluster Model panel. Wraps
/// <see cref="ClusterModelAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) with the same "singleton + Changed event + best-effort,
/// reachability-tolerant" shape as <see cref="BenchmarkDataStore"/>, so the UI survives tab switches and
/// degrades gracefully when the proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class ClusterModelAdminStore : IDisposable
{
    private readonly IClusterModelAdminClient _client;
    private readonly IDisposable? _ownedClient;
    private readonly ILogger<ClusterModelAdminStore>? _logger;

    /// <summary>Initializes a new instance of the <see cref="ClusterModelAdminStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">The proxy's TLS gRPC endpoint; defaults to <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.</param>
    public ClusterModelAdminStore(
        ILogger<ClusterModelAdminStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        _logger = logger;
        var client = new ClusterModelAdminClient(serverAddress);
        _client = client;
        _ownedClient = client;
        ServerAddress = serverAddress;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterModelAdminStore"/> class over a caller-supplied
    /// client. The seam tests use to drive the store without a live proxy; the caller owns the client's
    /// lifetime.
    /// </summary>
    public ClusterModelAdminStore(IClusterModelAdminClient client, ILogger<ClusterModelAdminStore>? logger = null)
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

    /// <summary>The cluster model's last-known status, or <see langword="null"/> before the first load.</summary>
    public ClusterModelStatusInfo? Status { get; private set; }

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load or mutation reached the proxy.</summary>
    /// <remarks>Same connectivity-only meaning as <see cref="PriceSourceStore.IsReachable"/>.</remarks>
    public bool IsReachable { get; private set; }

    /// <summary>The message from the last failure to reach the proxy, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether a retrain is currently running, so the UI can disable the button and show progress.</summary>
    public bool IsRetraining { get; private set; }

    /// <summary>The number of OOD bootstrap tasks embedded so far by a running retrain. Reset at the start of each retrain.</summary>
    public int BootstrapTasksEmbedded { get; private set; }

    /// <summary>The most recent retrain's outcome message, or <see langword="null"/> before any retrain has run this session.</summary>
    public string? LastRetrainMessage { get; private set; }

    /// <summary>Raised after any of the above change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the cluster model's current status. Failures are swallowed and surfaced via
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
        catch (ClusterModelAdminException ex)
        {
            IsReachable = !ex.IsUnavailable;
            LastError = ex.Message;
            _logger?.LogWarning(ex, "Failed to load the cluster model status from the router.");
        }
        finally
        {
            IsLoaded = true;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Runs a retrain, publishing bootstrap-embedding progress into <see cref="BootstrapTasksEmbedded"/> as
    /// it streams in and the final outcome plus status once it completes. <see cref="IsRetraining"/> is true
    /// for the duration.
    /// </summary>
    /// <exception cref="ClusterModelAdminException">The retrain could not be started or the router is unreachable.</exception>
    public async Task RetrainAsync(CancellationToken cancellationToken = default)
    {
        IsRetraining = true;
        BootstrapTasksEmbedded = 0;
        LastRetrainMessage = null;
        Changed?.Invoke();

        try
        {
            await foreach (var retrainEvent in _client.RetrainAsync(cancellationToken))
            {
                if (retrainEvent.BootstrapProgress is { } progress)
                {
                    BootstrapTasksEmbedded = progress.TasksEmbedded;
                }
                else if (retrainEvent.Result is { } result)
                {
                    LastRetrainMessage = result.Message;
                    Status = result.Status;
                }

                Changed?.Invoke();
            }

            IsReachable = true;
            IsLoaded = true;
            LastError = null;
        }
        catch (ClusterModelAdminException ex)
        {
            RecordFailure(ex);
            throw;
        }
        finally
        {
            // In a finally so a failed retrain re-enables the button rather than leaving it stuck disabled.
            // The exception still propagates for the panel to render.
            IsRetraining = false;
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
    private void RecordFailure(ClusterModelAdminException ex)
    {
        if (!ex.IsUnavailable)
        {
            return;
        }

        IsReachable = false;
        LastError = ex.Message;
        _logger?.LogWarning(ex, "The router became unreachable during a cluster-model operation.");
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose() => _ownedClient?.Dispose();
}
