namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// One routed request, received live over the proxy's <c>TelemetryService.StreamEvents</c> gRPC
/// stream. Constructed by <c>TotallyHot.ArcRouter.Gui.Services.LiveDataStore</c> from the generated
/// <c>TotallyHot.ArcRouter.Telemetry.Contract.RoutingTelemetryEvent</c> message - compiled from the same
/// <c>Protos/telemetry.proto</c> file the proxy's server-side code compiles, but *in this project*
/// rather than in TotallyHot.ArcRouter.Gui itself (see the csproj remarks for why: .NET MAUI's SingleProject
/// build doesn't reliably run Grpc.Tools' codegen). Not deserialized directly into this DTO - kept as
/// a separate, hand-written record so <see cref="ConversationAggregator"/>'s pure aggregation logic
/// stays decoupled from the wire message shape (proto3 optional-field presence, protobuf
/// <c>Timestamp</c>, decimal-as-string) and testable without constructing real protobuf messages.
/// </summary>
public sealed record RoutingTelemetryEventDto(
    string SessionId,
    int TurnNumber,
    bool IsSessionSynthesized,
    string RequestedModel,
    string ResolvedModel,
    string Provider,
    bool IsFallback,
    int? PromptTokens,
    int? CompletionTokens,
    decimal? EstimatedCostUsd,
    bool IsStreaming,
    long LatencyToHeadersMs,
    long TotalDurationMs,
    int StatusCode,
    DateTimeOffset TimestampUtc,
    string RoutedModel,
    int? CacheCreationTokens = null,
    int? CacheReadTokens = null,
    string? RequestSummary = null,
    string? ResponseSummary = null,
    string? CostConfidence = null,
    int RouterTokens = 0,
    decimal RouterCostUsd = 0m,
    string? SubstitutionReason = null);

