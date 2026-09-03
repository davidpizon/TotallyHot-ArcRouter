using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Fan-out registry for the telemetry gRPC stream (<see cref="TelemetryGrpcService"/>), replacing
/// SignalR's <c>IHubContext.Clients.All</c> broadcast primitive - see
/// docs/router/grpc-migration.md. Each active <c>StreamEvents</c> call registers its own
/// <see cref="ChannelWriter{T}"/> here for that call's lifetime; <see cref="Publish(RoutingTelemetryEvent)"/>,
/// <see cref="PublishLogLine"/>, and <see cref="Publish(QualitySignalEvent)"/> map the domain event to
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
    private readonly ILogger<TelemetryBroadcaster>? _logger;
    private readonly ConcurrentDictionary<ChannelWriter<TelemetryEvent>, byte> _writers = new();

    /// <param name="logger">Optional; used only to debug-log best-effort write failures, never to fail a publish.</param>
    public TelemetryBroadcaster(ILogger<TelemetryBroadcaster>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Registers a writer to receive every subsequently published event, for one <c>StreamEvents</c> call's lifetime.</summary>
    public void Register(ChannelWriter<TelemetryEvent> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writers[writer] = 0;
    }

    /// <summary>Unregisters a writer, e.g. when its <c>StreamEvents</c> call ends (client disconnect or cancellation).</summary>
    public void Unregister(ChannelWriter<TelemetryEvent> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writers.TryRemove(key: writer, value: out _);
    }

    /// <summary>
    /// Publishes a routing telemetry event to every registered writer. Must never throw and must
    /// never meaningfully block the caller - same fault-isolation contract
    /// <see cref="ITelemetryPublisher.PublishAsync"/> documents.
    /// </summary>
    public void Publish(RoutingTelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        WriteToAll(new TelemetryEvent { RoutingTelemetry = ToWire(telemetryEvent) });
    }

    /// <summary>
    /// Publishes a log line to every registered writer. Same fault-isolation contract as
    /// <see cref="Publish(RoutingTelemetryEvent)"/>.
    /// </summary>
    public void PublishLogLine(LogLineEvent logLine)
    {
        ArgumentNullException.ThrowIfNull(logLine);
        WriteToAll(new TelemetryEvent { LogLine = ToWire(logLine) });
    }

    /// <summary>
    /// Publishes a quality signal to every registered writer, for the dashboard's live
    /// verification tile. Same fault-isolation contract as <see cref="Publish(RoutingTelemetryEvent)"/>.
    /// </summary>
    public void Publish(QualitySignalEvent signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        WriteToAll(new TelemetryEvent { QualitySignal = ToWire(signal) });
    }

    /// <summary>
    /// Writes the envelope to every registered writer's bounded channel, isolating each writer's
    /// failures so a disconnected or gone client never disrupts the others or the caller.
    /// </summary>
    private void WriteToAll(TelemetryEvent envelope)
    {
        foreach (var writer in _writers.Keys)
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
                _logger?.LogDebug(exception: ex,
                    message: "Failed to write a telemetry event to a registered stream; continuing without it.");
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
            RoutedModel = e.RoutedModel,
            SubstitutionReason = e.SubstitutionReason.ToString(),
            Provider = e.Provider,
            IsFallback = e.IsFallback,
            IsStreaming = e.IsStreaming,
            LatencyToHeadersMs = e.LatencyToHeadersMs,
            TotalDurationMs = e.TotalDurationMs,
            StatusCode = e.StatusCode,
            TimestampUtc = Timestamp.FromDateTimeOffset(e.TimestampUtc),

            // Always set, unlike the optional fields below: the router's own consumption is a known
            // measurement even when it is zero (see RoutingTelemetryEvent.RouterTokens), so there is no
            // "absent" case to represent - writing them unconditionally keeps a zero on the wire as a
            // stated zero rather than something a receiver has to infer.
            RouterTokens = e.RouterTokens,
            RouterCostUsd = e.RouterCostUsd.ToString(CultureInfo.InvariantCulture)
        };

        if (e.PromptTokens is int promptTokens) wire.PromptTokens = promptTokens;

        if (e.CompletionTokens is int completionTokens) wire.CompletionTokens = completionTokens;

        if (e.CacheCreationTokens is int cacheCreationTokens) wire.CacheCreationTokens = cacheCreationTokens;

        if (e.CacheReadTokens is int cacheReadTokens) wire.CacheReadTokens = cacheReadTokens;

        if (e.EstimatedCostUsd is decimal estimatedCostUsd)
            // Decimal-as-string, not double: see "Decimal encoding" in docs/router/grpc-migration.md.
            wire.EstimatedCostUsd = estimatedCostUsd.ToString(CultureInfo.InvariantCulture);

        if (e.RequestSummary is not null) wire.RequestSummary = e.RequestSummary;

        if (e.ResponseSummary is not null) wire.ResponseSummary = e.ResponseSummary;

        // Always set, unlike the nullable fields above: CostConfidence is a non-nullable enum on the C#
        // side - RoutingTelemetryEvent's constructor defaults it to Unknown when the caller omits it - so
        // there is always a value to encode.
        wire.CostConfidence = e.CostConfidence.ToString();

        return wire;
    }

    /// <summary>
    /// Converts a <see cref="LogLineEvent"/> into its gRPC wire representation.
    /// </summary>
    private static Contract.LogLineEvent ToWire(LogLineEvent e)
    {
        return new Contract.LogLineEvent
        {
            TimestampUtc = Timestamp.FromDateTimeOffset(e.TimestampUtc),
            Level = e.Level,
            Message = e.Message
        };
    }

    /// <summary>
    /// Converts a <see cref="QualitySignalEvent"/> into its gRPC wire representation.
    /// </summary>
    private static Contract.QualitySignalEvent ToWire(QualitySignalEvent e)
    {
        var wire = new Contract.QualitySignalEvent
        {
            CorrelationId = e.CorrelationId,
            SessionId = e.SessionId,
            Dimension = e.Dimension,
            Model = e.Model,
            Language = e.Language,
            SyntaxValid = e.SyntaxValid,
            SyntaxAuthoritative = e.SyntaxAuthoritative,
            UnifiedScore = e.UnifiedScore,
            TimestampUtc = Timestamp.FromDateTimeOffset(e.TimestampUtc)
        };

        // The three optional fields are left unset rather than defaulted, so a reader can tell "the
        // analyzers all abstained" and "the judge did not contribute" apart from a genuine score of zero.
        if (e.AnalysisScore is double analysisScore) wire.AnalysisScore = analysisScore;

        if (e.JudgeScore is double judgeScore) wire.JudgeScore = judgeScore;

        if (e.DegradedReason is { } degradedReason) wire.DegradedReason = degradedReason;

        return wire;
    }
}