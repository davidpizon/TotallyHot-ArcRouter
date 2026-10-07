using Serilog.Core;
using Serilog.Events;

namespace TotallyHot.ArcRouter.Logging;

/// <summary>
/// Resolves the DI-registered <see cref="BodyLogController"/> on first emit, matching
/// <c>DeferredTelemetryPublisher</c>: the Serilog pipeline is wired while the host is still being
/// built, so eagerly resolving the controller would re-enter DI mid-construction.
/// </summary>
internal sealed class DeferredBodyLogSink : ILogEventSink
{
    private readonly IServiceProvider _services;
    private BodyLogController? _resolved;

    /// <param name="services">Used to lazily resolve <see cref="BodyLogController"/> on first emit.</param>
    public DeferredBodyLogSink(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        (_resolved ??= _services.GetRequiredService<BodyLogController>()).Emit(logEvent);
    }
}
