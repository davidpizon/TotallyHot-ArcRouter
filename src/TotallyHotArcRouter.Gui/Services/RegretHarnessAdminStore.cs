using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Regret Harness panel. Wraps
/// <see cref="RegretHarnessAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) with the same "singleton + Changed event + best-effort,
/// reachability-tolerant" shape as <see cref="LogRegModelAdminStore"/>, so the UI survives tab switches
/// and degrades gracefully when the proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
public sealed class RegretHarnessAdminStore : IDisposable
{
    private readonly IRegretHarnessAdminClient _client;
    private readonly ILogger<RegretHarnessAdminStore>? _logger;
    private readonly IDisposable? _ownedClient;

    /// <summary>Initializes a new instance of the <see cref="RegretHarnessAdminStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public RegretHarnessAdminStore(
        ILogger<RegretHarnessAdminStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        _logger = logger;
        var client = new RegretHarnessAdminClient(serverAddress);
        _client = client;
        _ownedClient = client;
        ServerAddress = serverAddress;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RegretHarnessAdminStore"/> class over a
    /// caller-supplied client. The seam tests use to drive the store without a live proxy; the caller
    /// owns the client's lifetime.
    /// </summary>
    public RegretHarnessAdminStore(IRegretHarnessAdminClient client, ILogger<RegretHarnessAdminStore>? logger = null)
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

    /// <summary>The last completed run's status, or <see langword="null"/> before the first load.</summary>
    public RegretHarnessStatusInfo? Status { get; private set; }

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether the last load or run reached the proxy.</summary>
    /// <remarks>Same connectivity-only meaning as <see cref="PriceSourceStore.IsReachable"/>.</remarks>
    public bool IsReachable { get; private set; }

    /// <summary>The message from the last failure to reach the proxy, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether a run is currently in progress, so the UI can disable the button and show progress.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The coarse stage a running harness is currently in, or <see langword="null"/> before any progress has been reported.</summary>
    public RegretHarnessStageInfo? CurrentStage { get; private set; }

    /// <summary>The most recent run's outcome message, or <see langword="null"/> before any run has completed this session.</summary>
    public string? LastRunMessage { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        _ownedClient?.Dispose();
    }

    /// <summary>Raised after any of the above change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the last completed run's status. Failures are swallowed and surfaced via
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
        catch (RegretHarnessAdminException ex)
        {
            IsReachable = !ex.IsUnavailable;
            LastError = ex.Message;
            _logger?.LogWarning(exception: ex, message: "Failed to load the regret harness status from the router.");
        }
        finally
        {
            IsLoaded = true;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Runs the harness, publishing stage progress into <see cref="CurrentStage"/> as it streams in and
    /// the final outcome once it completes. <see cref="IsRunning"/> is true for the duration.
    /// </summary>
    /// <exception cref="RegretHarnessAdminException">The run could not be started or the router is unreachable.</exception>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = true;
        CurrentStage = null;
        LastRunMessage = null;
        Changed?.Invoke();

        try
        {
            await foreach (var runEvent in _client.RunAsync(cancellationToken))
            {
                if (runEvent.StageProgress is { } stage)
                {
                    CurrentStage = stage;
                }
                else if (runEvent.Result is { } result)
                {
                    LastRunMessage = result.Message;
                    if (result.Kind == RegretHarnessRunResultKindInfo.Completed)
                        Status = new RegretHarnessStatusInfo(HasRun: true, result.RanAtUtc, result.Message,
                            result.Splits);
                }

                Changed?.Invoke();
            }

            IsReachable = true;
            IsLoaded = true;
            LastError = null;
        }
        catch (RegretHarnessAdminException ex)
        {
            RecordFailure(ex);
            throw;
        }
        finally
        {
            // In a finally so a failed run re-enables the button rather than leaving it stuck disabled.
            // The exception still propagates for the panel to render.
            IsRunning = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Reflects a failed mutation in the store's state before the caller rethrows, so
    /// <see cref="IsReachable"/> keeps its documented meaning after a run and not only after a load.
    /// </summary>
    private void RecordFailure(RegretHarnessAdminException ex)
    {
        if (!ex.IsUnavailable) return;

        IsReachable = false;
        LastError = ex.Message;
        _logger?.LogWarning(exception: ex, message: "The router became unreachable during a regret harness run.");
        Changed?.Invoke();
    }
}
