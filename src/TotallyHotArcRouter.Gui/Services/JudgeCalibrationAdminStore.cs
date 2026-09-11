using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model backing the Governance tab's Judge Calibration panel
/// (docs/router/geval-shadow-scoring-plan.md Phase G2). Wraps
/// <see cref="JudgeCalibrationAdminClient"/> (the tested, platform-agnostic logic in
/// TotallyHot.ArcRouter.Gui.Telemetry) in the shared <see cref="AdminStoreBase{TClient}"/> shape, so the
/// UI survives tab switches and degrades gracefully when the proxy isn't running. Registered in
/// <c>MauiProgram</c>.
/// </summary>
/// <remarks>
/// Simpler than its siblings by one whole axis: there is no <c>IsRunning</c> and no run method, because
/// the report has no run to start. The server recomputes on every read, so this store's only operation is
/// a load - which is also what the panel's Refresh button calls.
/// </remarks>
public sealed class JudgeCalibrationAdminStore : AdminStoreBase<IJudgeCalibrationAdminClient>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JudgeCalibrationAdminStore"/> class, creating and
    /// owning a client to <paramref name="serverAddress"/>.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serverAddress">
    /// The proxy's TLS gRPC endpoint; defaults to
    /// <see cref="TelemetryChannelFactory.DefaultServerAddress"/>.
    /// </param>
    public JudgeCalibrationAdminStore(
        ILogger<JudgeCalibrationAdminStore>? logger = null,
        string serverAddress = TelemetryChannelFactory.DefaultServerAddress)
        : base(client: new JudgeCalibrationAdminClient(serverAddress), logger: logger, ownsClient: true)
    {
        ServerAddress = serverAddress;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JudgeCalibrationAdminStore"/> class over a
    /// caller-supplied client. The seam tests use to drive the store without a live proxy; the caller
    /// owns the client's lifetime.
    /// </summary>
    /// <param name="client">The admin client to read through.</param>
    /// <param name="logger">Optional logger.</param>
    public JudgeCalibrationAdminStore(IJudgeCalibrationAdminClient client,
        ILogger<JudgeCalibrationAdminStore>? logger = null)
        : base(client: client, logger: logger)
    {
    }

    /// <summary>
    /// The proxy endpoint this store's client talks to, so the unreachable state can name the address it
    /// actually failed to reach rather than assuming the default. <see langword="null"/> when constructed
    /// over a caller-supplied client, whose endpoint this store has no way to know.
    /// </summary>
    public string? ServerAddress { get; }

    /// <summary>The most recently computed report, or <see langword="null"/> before the first successful load.</summary>
    public JudgeCalibrationReportInfo? Report { get; private set; }

    /// <summary>Whether a load is currently in flight, so the UI can disable Refresh while one runs.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Recomputes and loads the calibration report. Failures are swallowed and surfaced via
    /// <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>
    /// rather than thrown, so the tab renders an error state instead of crashing when the proxy isn't
    /// running.
    /// </summary>
    /// <remarks>
    /// <see cref="Report"/> is cleared on every failure, including a reachable one (a server-side
    /// exception, a bad request) - not only an unreachable-router failure. Every call recomputes from
    /// current rows (this store's whole reason to exist, per its own remarks), so a report that failed to
    /// recompute must never be left standing in for the one that would have replaced it: a refresh that
    /// silently keeps showing yesterday's verdict while
    /// <see cref="AdminStoreBase{TClient}.LastError"/> goes unread by the caller is a worse
    /// failure mode than an honest "could not load" state. The base runs that cleanup before raising its
    /// change notification, so no subscriber ever sees the stale report.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        NotifyChanged();

        await LoadGuardedAsync(
            async ct => Report = await Client.GetReportAsync(ct),
            "load the judge calibration report",
            cancellationToken,
            onFailure: () => Report = null,
            // Clearing IsLoading here, right before LoadGuardedAsync's own completion notification, means
            // that notification is also this call's "the Refresh button can re-enable" notification -
            // instead of a third one carrying an intermediate "finished but still loading" state that no
            // subscriber should ever see. Runs whether the load succeeds or fails, matching
            // RegretHarnessAdminStore.RunAsync's reasoning for re-enabling on failure too.
            beforeNotify: () => IsLoading = false);
    }
}
