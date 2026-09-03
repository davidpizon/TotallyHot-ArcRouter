namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// An <see cref="ITelemetryPublisher"/> that resolves the real, DI-registered publisher lazily, on
/// first actual publish call, instead of at construction time.
/// </summary>
/// <remarks>
/// Exists specifically to safely wire <see cref="TelemetryLogEventSink"/> into Serilog's pipeline in
/// <c>Program.cs</c>. Eagerly resolving <see cref="ITelemetryPublisher"/> from inside
/// <c>UseSerilog</c>'s configuration callback re-enters DI while the logging system itself is still
/// being built: <see cref="TelemetryPublisher"/>'s constructor takes an <c>ILogger&lt;TelemetryPublisher&gt;</c>,
/// resolving which requires <c>ILoggerFactory</c> - the very singleton whose factory delegate is
/// currently running. That circular resolution breaks the whole Serilog pipeline (not just this
/// sink), silently disabling every configured sink, including the plain <c>Console</c> one. Deferring
/// resolution until the first log event flows through - by which point the host is fully built and
/// the cycle no longer exists - avoids this entirely.
/// </remarks>
public sealed class DeferredTelemetryPublisher : ITelemetryPublisher
{
    private readonly IServiceProvider _serviceProvider;
    private ITelemetryPublisher? _resolved;

    /// <param name="serviceProvider">Used to lazily resolve the real <see cref="ITelemetryPublisher"/> on first publish.</param>
    public DeferredTelemetryPublisher(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Gets the real, DI-registered <see cref="ITelemetryPublisher"/>, resolving and caching it on first access.
    /// </summary>
    private ITelemetryPublisher Resolved => _resolved ??= _serviceProvider.GetRequiredService<ITelemetryPublisher>();

    /// <inheritdoc/>
    public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        return Resolved.PublishAsync(telemetryEvent: telemetryEvent, cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default)
    {
        return Resolved.PublishLogLineAsync(logLine: logLine, cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public Task PublishQualitySignalAsync(QualitySignalEvent signal, CancellationToken cancellationToken = default)
    {
        return Resolved.PublishQualitySignalAsync(signal: signal, cancellationToken: cancellationToken);
    }
}