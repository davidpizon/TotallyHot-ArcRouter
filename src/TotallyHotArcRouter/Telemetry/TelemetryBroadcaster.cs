using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Fan-out registry for the telemetry gRPC stream (<see cref="TelemetryGrpcService"/>), replacing
/// SignalR's <c>IHubContext.Clients.All</c> broadcast primitive - see
/// docs/router/grpc-migration.md. Each active <c>StreamEvents</c> call registers its own
/// <see cref="ChannelWriter{T}"/> here for that call's lifetime; <see cref="Publish(RoutingTelemetryEvent)"/>,
/// <see cref="PublishLogLine"/>, and <see cref="Publish(SandboxSignalEvent)"/> map the domain event to
/// the wire <see cref="Contract.TelemetryEvent"/> envelope and write it to every registered writer.
/// </summary>
/// <remarks>
/// Unlike the former SignalR <c>TelemetryHub</c>'s <c>IHubContext</c> (only available once the inner
/// Kestrel host has started - see the SignalR-era <c>TelemetryPublisher.AttachHubContext</c>), this
/// class has no hosting dependency at all: it's plain <see cref="System.Threading.Channels"/>
/// bookkeeping. It's constructed once as an outer-container singleton and the same instance is
/// registered into <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>'s separate inner-host DI container so
/// <see cref="TelemetryGrpcService"/> can receive it - no post-start attachment step needed. Before
/// any client has connected (or if none ever does, e.g. in tests), publishing is naturally a no-op:
/// there's simply nothing registered to write to.
/// </remarks>
public sealed class TelemetryBroadcaster
{
    private readonly ConcurrentDictionary<ChannelWriter<Contract.TelemetryEvent>, byte> _writers = new();
    private readonly ILogger<TelemetryBroadcaster>? _logger;

    /// <param name="logger">Optional; used only to debug-log best-effort write failures, never to fail a publish.</param>
    public TelemetryBroadcaster(ILogger<TelemetryBroadcaster>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Registers a writer to receive every subsequently published event, for one <c>StreamEvents</c> call's lifetime.</summary>
    public void Register(ChannelWriter<Contract.TelemetryEvent> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writers[writer] = 0;
    }

    /// <summary>Unregisters a writer, e.g. when its <c>StreamEvents</c> call ends (client disconnect or cancellation).</summary>
    public void Unregister(ChannelWriter<Contract.TelemetryEvent> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writers.TryRemove(writer, out _);
    }

    /// <summary>
    /// Publishes a routing telemetry event to every registered writer. Must never throw and must
    /// never meaningfully block the caller - same fault-isolation contract
    /// <see cref="ITelemetryPublisher.PublishAsync"/> documents.
    /// </summary>
    public void Publish(RoutingTelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        WriteToAll(new Contract.TelemetryEvent { RoutingTelemetry = ToWire(telemetryEvent) });
    }

    /// <summary>Publishes a log line to every registered writer. Same fault-isolation contract as <see cref="Publish(RoutingTelemetryEvent)"/>.</summary>
    public void PublishLogLine(LogLineEvent logLine)
    {
        ArgumentNullException.ThrowIfNull(logLine);
        WriteToAll(new Contract.TelemetryEvent { LogLine = ToWire(logLine) });
    }

    /// <summary>
    /// Publishes a sandbox execution signal to every registered writer, for the dashboard's live
    /// verification tile. Same fault-isolation contract as <see cref="Publish(RoutingTelemetryEvent)"/>.
    /// </summary>
    public void Publish(SandboxSignalEvent signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        WriteToAll(new Contract.TelemetryEvent { SandboxSignal = ToWire(signal) });
    }

    /// <summary>
    /// Writes the envelope to every registered writer's bounded channel, isolating each writer's
    /// failures so a disconnected or gone client never disrupts the others or the caller.
    /// </summary>
    private void WriteToAll(Contract.TelemetryEvent envelope)
    {
        foreach (var writer in _writers.Keys)
        {
            try
            {
                // Every registered writer wraps a bounded, drop-oldest-when-full Channel<T> (see
                // TelemetryGrpcService), so TryWrite is always synchronous and non-blocking - it
                // either succeeds outright, makes room by dropping the oldest queued event, or (only
                // once that call has already ended) silently fails.
                writer.TryWrite(envelope);
            }
            catch (Exception ex)
            {
                // Telemetry is best-effort observability, never a request-handling dependency: a
                // disconnected/gone client must never surface here.
                _logger?.LogDebug(ex, "Failed to write a telemetry event to a registered stream; continuing without it.");
            }
        }
    }

    /// <summary>
    /// Converts a <see cref="RoutingTelemetryEvent"/> into its gRPC wire representation, setting
    /// optional fields only when present so proto3 presence tracking reflects the source event.
    /// </summary>
    private static Contract.RoutingTelemetryEvent ToWire(RoutingTelemetryEvent e)
    {
        var wire = new Contract.RoutingTelemetryEvent
        {
            SessionId = e.SessionId,
            TurnNumber = e.TurnNumber,
            IsSessionSynthesized = e.IsSessionSynthesized,
            RequestedModel = e.RequestedModel,
            ResolvedModel = e.ResolvedModel,
            Provider = e.Provider,
            IsFallback = e.IsFallback,
            IsStreaming = e.IsStreaming,
            LatencyToHeadersMs = e.LatencyToHeadersMs,
            TotalDurationMs = e.TotalDurationMs,
            StatusCode = e.StatusCode,
            TimestampUtc = Timestamp.FromDateTimeOffset(e.TimestampUtc),
        };

        if (e.PromptTokens is int promptTokens)
        {
            wire.PromptTokens = promptTokens;
        }

        if (e.CompletionTokens is int completionTokens)
        {
            wire.CompletionTokens = completionTokens;
        }

        if (e.CacheCreationTokens is int cacheCreationTokens)
        {
            wire.CacheCreationTokens = cacheCreationTokens;
        }

        if (e.CacheReadTokens is int cacheReadTokens)
        {
            wire.CacheReadTokens = cacheReadTokens;
        }

        if (e.EstimatedCostUsd is decimal estimatedCostUsd)
        {
            // Decimal-as-string, not double: see "Decimal encoding" in docs/router/grpc-migration.md.
            wire.EstimatedCostUsd = estimatedCostUsd.ToString(CultureInfo.InvariantCulture);
        }

        if (e.RequestSummary is not null)
        {
            wire.RequestSummary = e.RequestSummary;
        }

        if (e.ResponseSummary is not null)
        {
            wire.ResponseSummary = e.ResponseSummary;
        }

        // Always set, unlike the nullable fields above: CostConfidence is a non-nullable enum on the C#
        // side - RoutingTelemetryEvent's constructor defaults it to Unknown when the caller omits it - so
        // there is always a value to encode.
        wire.CostConfidence = e.CostConfidence.ToString();

        return wire;
    }

    /// <summary>
    /// Converts a <see cref="LogLineEvent"/> into its gRPC wire representation.
    /// </summary>
    private static Contract.LogLineEvent ToWire(LogLineEvent e) => new()
    {
        TimestampUtc = Timestamp.FromDateTimeOffset(e.TimestampUtc),
        Level = e.Level,
        Message = e.Message,
    };

    /// <summary>
    /// Converts a <see cref="SandboxSignalEvent"/> into its gRPC wire representation.
    /// </summary>
    private static Contract.SandboxSignalEvent ToWire(SandboxSignalEvent e)
    {
        var wire = new Contract.SandboxSignalEvent
        {
            CorrelationId = e.CorrelationId,
            SessionId = e.SessionId,
            Dimension = e.Dimension,
            Model = e.Model,
            Language = e.Language,
            Tier = e.Tier,
            SyntaxValid = e.SyntaxValid,
            Executed = e.Executed,
            TimedOut = e.TimedOut,
            UnifiedScore = e.UnifiedScore,
            WallClockMs = e.WallClockMs,
            PeakMemoryBytes = e.PeakMemoryBytes,
            TimestampUtc = Timestamp.FromDateTimeOffset(e.TimestampUtc),
        };

        if (e.ExitCode is int exitCode)
        {
            wire.ExitCode = exitCode;
        }

        return wire;
    }
}

