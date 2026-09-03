using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Threading.Channels;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// gRPC service dashboards connect to for live routing telemetry, log lines, and (via
/// <see cref="ListPersistedSessions"/>) persisted session history. Replaces the former SignalR
/// <c>TelemetryHub</c> - see docs/router/grpc-migration.md. Mapped by
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>.
/// </summary>
public sealed class TelemetryGrpcService : Contract.TelemetryService.TelemetryServiceBase
{
    private readonly TelemetryBroadcaster _broadcaster;
    private readonly ITranscriptStore _transcriptStore;
    private readonly IOptionsMonitor<TranscriptOptions> _transcriptOptions;

    /// <param name="broadcaster">Registers/unregisters each call's channel writer and receives published events.</param>
    /// <param name="transcriptStore">Backs <see cref="ListPersistedSessions"/> with persisted <c>request_transcripts</c> rows.</param>
    /// <param name="transcriptOptions">Supplies the live <see cref="TranscriptOptions.Enabled"/> gate for <see cref="ListPersistedSessions"/>'s response.</param>
    public TelemetryGrpcService(
        TelemetryBroadcaster broadcaster,
        ITranscriptStore transcriptStore,
        IOptionsMonitor<TranscriptOptions> transcriptOptions)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(transcriptOptions);
        _broadcaster = broadcaster;
        _transcriptStore = transcriptStore;
        _transcriptOptions = transcriptOptions;
    }

    /// <summary>
    /// Per-call channel capacity. Bounded (not unbounded) so a stalled or unusually slow client
    /// can't make its buffered backlog grow without limit while <see cref="TelemetryBroadcaster"/>
    /// keeps publishing - telemetry is explicitly best-effort (see
    /// <see cref="ITelemetryPublisher.PublishAsync"/>'s contract), so <see cref="BoundedChannelFullMode.DropOldest"/>
    /// discards the stalest buffered event to make room rather than blocking the publisher or
    /// growing memory - a live dashboard cares about catching up to *current* state, not replaying
    /// every dropped event once a slow client catches up.
    /// </summary>
    private const int ChannelCapacity = 1024;

    /// <summary>
    /// Streams every event <see cref="TelemetryBroadcaster"/> publishes to this one connected client,
    /// for the lifetime of the call - the gRPC equivalent of SignalR's push-only hub connection.
    /// gRPC server-streaming has no built-in "broadcast to every call" primitive the way
    /// <c>IHubContext.Clients.All</c> did, so each call registers its own bounded
    /// <see cref="Channel{T}"/> with the shared <see cref="TelemetryBroadcaster"/> for its duration.
    /// </summary>
    public override async Task StreamEvents(
        Contract.StreamEventsRequest request,
        IServerStreamWriter<Contract.TelemetryEvent> responseStream,
        ServerCallContext context)
    {
        var channel = Channel.CreateBounded<Contract.TelemetryEvent>(
            new BoundedChannelOptions(ChannelCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
        _broadcaster.Register(channel.Writer);
        try
        {
            await foreach (var telemetryEvent in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(telemetryEvent);
            }
        }
        finally
        {
            _broadcaster.Unregister(channel.Writer);
        }
    }

    /// <summary>
    /// Returns the most recent persisted <c>request_transcripts</c> rows for the GUI Sessions tab
    /// (docs/router/sessions-tab-training-data-plan.md Phase 1). Reads
    /// <see cref="TranscriptOptions.Enabled"/> itself, rather than trusting an empty transcript list to
    /// imply capture is off, so the response can tell the two states apart:
    /// <see cref="Contract.ListPersistedSessionsResponse.TranscriptCaptureEnabled"/> false means capture is
    /// off, not that no traffic has been persisted yet.
    /// </summary>
    public override async Task<Contract.ListPersistedSessionsResponse> ListPersistedSessions(
        Contract.ListPersistedSessionsRequest request,
        ServerCallContext context)
    {
        var enabled = _transcriptOptions.CurrentValue.Enabled;
        var response = new Contract.ListPersistedSessionsResponse { TranscriptCaptureEnabled = enabled };

        if (!enabled)
        {
            return response;
        }

        var transcripts = await _transcriptStore.ListSessionsAsync(request.Limit, context.CancellationToken)
            .ConfigureAwait(false);

        response.Transcripts.AddRange(transcripts.Select(ToContract));
        return response;
    }

    /// <summary>Maps one <see cref="SessionTranscript"/> onto its wire representation.</summary>
    private static Contract.PersistedTranscript ToContract(SessionTranscript transcript)
    {
        var contract = new Contract.PersistedTranscript
        {
            SessionId = transcript.SessionId,
            CorrelationId = transcript.CorrelationId,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(transcript.CreatedAtUtc),
            RequestedModel = transcript.RequestedModel,
            RoutedModel = transcript.RoutedModel,
        };

        if (transcript.PromptText is { } promptText)
        {
            contract.PromptText = promptText;
        }

        if (transcript.ResponseText is { } responseText)
        {
            contract.ResponseText = responseText;
        }

        if (transcript.Cost is { } cost)
        {
            contract.CostUsd = cost.ToString(CultureInfo.InvariantCulture);
        }

        if (transcript.InputTokens is { } inputTokens)
        {
            contract.InputTokens = inputTokens;
        }

        if (transcript.OutputTokens is { } outputTokens)
        {
            contract.OutputTokens = outputTokens;
        }

        if (transcript.MemoryEntryId is { } memoryEntryId)
        {
            contract.MemoryEntryId = memoryEntryId;
        }

        return contract;
    }
}

