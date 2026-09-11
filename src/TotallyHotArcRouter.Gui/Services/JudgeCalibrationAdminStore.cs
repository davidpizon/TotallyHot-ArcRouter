using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Judge Calibration panel
/// (docs/router/geval-shadow-scoring-plan.md Phase G2). Wraps
/// <see cref="JudgeCalibrationAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) with the same "singleton + Changed event + best-effort,
/// reachability-tolerant" shape as <see cref="RegretHarnessAdminStore"/>, so the UI survives tab switches
/// and degrades gracefully when the proxy isn't running. Registered in <c>MauiProgram</c>.
/// </summary>
/// <remarks>
/// Simpler than its siblings by one whole axis: there is no <c>IsRunning</c> and no run method, because
/// the report has no run to start. The server recomputes on every read, so this store's only operation is
/// a load - which is also what the panel's Refresh button calls.
/// </remarks>
public sealed class JudgeCalibrationAdminStore : IDisposable
{
    private readonly IJudgeCalibrationAdminClient _client;
    private readonly ILogger<JudgeCalibrationAdminStore>? _logger;
    private readonly IDisposable? _ownedClient;

    /// <summary>Initializes a new instance of the <see cref="JudgeCalibrationAdminStore"/> class.</summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public JudgeCalibrationAdminStore(
        ILogger<JudgeCalibrationAdminStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
    {
        _logger = logger;
        var client = new JudgeCalibrationAdminClient(serverAddress);
        _client = client;
        _ownedClient = client;
        ServerAddress = serverAddress;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JudgeCalibrationAdminStore"/> class over a
    /// caller-supplied client. The seam tests use to drive the store without a live proxy; the caller
    /// owns the client's lifetime.
    /// </summary>
    public JudgeCalibrationAdminStore(IJudgeCalibrationAdminClient client,
        ILogger<JudgeCalibrationAdminStore>? logger = null)
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

    /// <summary>The most recently computed report, or <see langword="null"/> before the first successful load.</summary>
    public JudgeCalibrationReportInfo? Report { get; private set; }

    /// <summary>Whether a load has completed at least once (so the UI can distinguish "loading" from "empty").</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Whether a load is currently in flight, so the UI can disable Refresh while one runs.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Whether the last load reached the proxy.</summary>
    /// <remarks>Same connectivity-only meaning as <see cref="PriceSourceStore.IsReachable"/>.</remarks>
    public bool IsReachable { get; private set; }

    /// <summary>The message from the last failure to reach the proxy, if any.</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        _ownedClient?.Dispose();
    }

    /// <summary>Raised after any of the above change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Recomputes and loads the calibration report. Failures are swallowed and surfaced via
    /// <see cref="IsReachable"/>/<see cref="LastError"/> rather than thrown, so the tab renders an error
    /// state instead of crashing when the proxy isn't running.
    /// </summary>
    /// <remarks>
    /// <see cref="Report"/> is cleared on every failure, including a reachable one (a server-side
    /// exception, a bad request) - not only an unreachable-router failure. Every call recomputes from
    /// current rows (this store's whole reason to exist, per its own remarks), so a report that failed to
    /// recompute must never be left standing in for the one that would have replaced it: a refresh that
    /// silently keeps showing yesterday's verdict while <see cref="LastError"/> goes unread by the caller
    /// is a worse failure mode than an honest "could not load" state.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        Changed?.Invoke();

        try
        {
            Report = await _client.GetReportAsync(cancellationToken);
            IsReachable = true;
            LastError = null;
        }
        catch (GrpcAdminException ex)
        {
            // Cleared unconditionally, not only when IsReachable ends up false: a reachable failure (the
            // router answered but the call itself failed) is just as much a reason to distrust the
            // previous report as an unreachable one is. IsReachable still records which kind of failure
            // this was, so the panel can tell "the router is down" from "the router answered with an
            // error" without ever risking a stale render in either case.
            Report = null;
            IsReachable = !ex.IsUnavailable;
            LastError = ex.Message;
            _logger?.LogWarning(exception: ex, message: "Failed to load the judge calibration report from the router.");
        }
        finally
        {
            // In a finally so a failed load re-enables the Refresh button rather than leaving it stuck
            // disabled, matching RegretHarnessAdminStore.RunAsync's reasoning.
            IsLoading = false;
            IsLoaded = true;
            Changed?.Invoke();
        }
    }
}
