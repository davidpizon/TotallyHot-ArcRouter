using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Publishes <see cref="RoutingTelemetryEvent"/>s to every connected dashboard.
/// </summary>
public interface ITelemetryPublisher
{
    /// <summary>
    /// Publishes an event. Must never throw and must never meaningfully block the caller (request
    /// forwarding must not slow down because a dashboard is slow to receive, or because no dashboard
    /// is connected at all) - implementations are responsible for fault isolation.
    /// </summary>
    Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a log line for the GUI's Console tab. Same never-throw, never-meaningfully-block
    /// contract as <see cref="PublishAsync"/> - callers include <see cref="TelemetryLogEventSink"/>,
    /// invoked synchronously from Serilog's logging pipeline, which must never be slowed down by a
    /// slow or absent dashboard.
    /// </summary>
    Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an optional sandbox execution signal for the dashboard's live verification tile.
    /// Existing implementers may ignore it.
    /// </summary>
    Task PublishSandboxSignalAsync(SandboxSignalEvent signal, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Default <see cref="ITelemetryPublisher"/>. A thin wrapper around <see cref="TelemetryBroadcaster"/>,
/// the fan-out registry <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>'s inner Kestrel host uses to
/// deliver events to every connected <see cref="TelemetryGrpcService.StreamEvents"/> call.
/// </summary>
/// <remarks>
/// Unlike the SignalR-era implementation, there's no separate "attach once the inner host starts"
/// step: <see cref="TelemetryBroadcaster"/> has no hosting dependency of its own, so the same instance
/// is simply constructed once as an outer-container singleton and shared with the inner host's DI
/// container (see <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>'s constructor remarks). Publishing is
/// naturally a safe no-op whenever nothing is registered to receive it - before any dashboard has
/// connected, or in tests that construct this without a running <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>.
/// </remarks>
public sealed class TelemetryPublisher : ITelemetryPublisher
{
    private readonly TelemetryBroadcaster _broadcaster;
    private readonly ILogger<TelemetryPublisher>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryPublisher"/> class.
    /// </summary>
    /// <param name="broadcaster">The fan-out registry to publish through.</param>
    /// <param name="logger">Optional logger. Publish failures are logged at Debug level, not surfaced, since they must never affect request handling.</param>
    public TelemetryPublisher(TelemetryBroadcaster broadcaster, ILogger<TelemetryPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        _broadcaster = broadcaster;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            _broadcaster.Publish(telemetryEvent);
        }
        catch (Exception ex)
        {
            // Telemetry is best-effort observability, never a request-handling dependency: any
            // broadcast failure must never surface here.
            _logger?.LogDebug(ex, "Failed to publish routing telemetry event; continuing without it.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default)
    {
        try
        {
            _broadcaster.PublishLogLine(logLine);
        }
        catch (Exception ex)
        {
            // Same fault-isolation contract as PublishAsync: a log-line publish failure must never
            // surface to (or block) the Serilog pipeline that triggered it.
            _logger?.LogDebug(ex, "Failed to publish log line; continuing without it.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PublishSandboxSignalAsync(SandboxSignalEvent signal, CancellationToken cancellationToken = default)
    {
        try
        {
            _broadcaster.Publish(signal);
        }
        catch (Exception ex)
        {
            // Same fault-isolation contract as PublishAsync: a sandbox-signal publish failure must
            // never surface to (or block) RouterMemoryScoreObserver's observation path.
            _logger?.LogDebug(ex, "Failed to publish sandbox signal; continuing without it.");
        }

        return Task.CompletedTask;
    }
}

