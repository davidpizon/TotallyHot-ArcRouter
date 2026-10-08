using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Threading.Channels;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using TotallyHot.ArcRouter.Telemetry.Contract;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// gRPC service dashboards connect to for live routing telemetry, log lines, and (via
/// <see cref="ListPersistedSessions"/>) persisted session history. Replaces the former SignalR
/// <c>TelemetryHub</c> - see docs/router/grpc-migration.md. Mapped by
/// <see cref="TotallyHot.ArcRouter.Proxy.ProxyServer"/>.
/// </summary>
public sealed class TelemetryGrpcService : TelemetryService.TelemetryServiceBase
{
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
    /// The row count <see cref="ListPersistedSessions"/> uses when the request leaves <c>limit</c> unset (0) -
    /// the same 500 rows the GUI's <c>PersistedSessionStore</c> asks for.
    /// </summary>
    private const int DefaultListLimit = 500;

    /// <summary>
    /// The largest row count <see cref="ListPersistedSessions"/> serves in one response; larger requests are
    /// clamped to it, as #176's plan does for its session RPC.
    /// </summary>
    private const int MaxListLimit = 2000;

    /// <summary>
    /// The most bytes a serialized <see cref="ListPersistedSessionsResponse"/> may take: three quarters of
    /// <c>Grpc.Net.Client</c>'s default 4 MiB receive cap, which every GUI channel runs at. The router itself
    /// sets no send cap, so without this budget a long history fails on the client with
    /// <see cref="StatusCode.ResourceExhausted"/> and the Sessions tab shows no persisted history (#179,
    /// ADR-0023). Previews keep a typical list far below it; the budget is what guarantees it.
    /// </summary>
    internal const int MaxListResponseBytes = 3 * 1024 * 1024;

    /// <summary>
    /// What <see cref="ListPersistedSessionsResponse.TranscriptCaptureEnabled"/> and
    /// <see cref="ListPersistedSessionsResponse.HasMore"/> cost when set: one tag byte and one value byte each.
    /// Reserved up front so setting <c>has_more</c> after the budget cut can't push the response past it.
    /// </summary>
    private const int ListResponseFlagBytes = 2 + 2;

    private readonly TelemetryBroadcaster _broadcaster;
    private readonly ILogger<TelemetryGrpcService> _logger;
    private readonly IOptionsMonitor<TranscriptOptions> _transcriptOptions;
    private readonly ITranscriptStore _transcriptStore;
    private readonly ContentGate? _contentGate;

    /// <param name="broadcaster">Registers/unregisters each call's channel writer and receives published events.</param>
    /// <param name="transcriptStore">Backs <see cref="ListPersistedSessions"/> with persisted <c>request_transcripts</c> rows.</param>
    /// <param name="transcriptOptions">
    /// Supplies the live <see cref="TranscriptOptions.Enabled"/> gate for
    /// <see cref="ListPersistedSessions"/>'s response.
    /// </param>
    /// <param name="logger">
    /// Records when <see cref="MaxListResponseBytes"/>, rather than the row limit, cut a
    /// <see cref="ListPersistedSessions"/> response. Optional so tests can omit it.
    /// </param>
    /// <param name="contentGate">
    /// The passkey content gate (ADR-0020). When supplied, conversation text - persisted prompt/response
    /// text, live request/response summaries, and content-bearing log lines - reaches only calls carrying a
    /// valid <c>x-content-grant</c> header. <see langword="null"/> disables the gate and serves text as
    /// before; the router always registers one, so only tests that exercise the telemetry plumbing alone
    /// omit it.
    /// </param>
    public TelemetryGrpcService(
        TelemetryBroadcaster broadcaster,
        ITranscriptStore transcriptStore,
        IOptionsMonitor<TranscriptOptions> transcriptOptions,
        ILogger<TelemetryGrpcService>? logger = null,
        ContentGate? contentGate = null)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(transcriptOptions);
        _broadcaster = broadcaster;
        _transcriptStore = transcriptStore;
        _transcriptOptions = transcriptOptions;
        _logger = logger ?? NullLogger<TelemetryGrpcService>.Instance;
        _contentGate = contentGate;
    }

    /// <summary>
    /// Streams every event <see cref="TelemetryBroadcaster"/> publishes to this one connected client,
    /// for the lifetime of the call - the gRPC equivalent of SignalR's push-only hub connection.
    /// gRPC server-streaming has no built-in "broadcast to every call" primitive the way
    /// <c>IHubContext.Clients.All</c> did, so each call registers its own bounded
    /// <see cref="Channel{T}"/> with the shared <see cref="TelemetryBroadcaster"/> for its duration.
    /// </summary>
    public override async Task StreamEvents(
        StreamEventsRequest request,
        IServerStreamWriter<TelemetryEvent> responseStream,
        ServerCallContext context)
    {
        var channel = Channel.CreateBounded<TelemetryEvent>(
            new BoundedChannelOptions(ChannelCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
        _broadcaster.Register(channel.Writer);
        try
        {
            await foreach (var telemetryEvent in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                var outgoing = ApplyContentGate(telemetryEvent, context);
                if (outgoing is not null) await responseStream.WriteAsync(outgoing);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _broadcaster.Unregister(channel.Writer);
        }
    }

    /// <summary>
    /// Applies the passkey content gate to one broadcast event for one stream (ADR-0020). The grant is
    /// re-checked per event, so a grant that expires or is revoked mid-stream stops text immediately. A
    /// content-bearing log line is dropped without a grant; a routing event's request/response summaries are
    /// stripped from a clone (the broadcast instance is shared by every stream and must not be mutated).
    /// </summary>
    /// <returns>The event to write, or <see langword="null"/> when this stream must not see it at all.</returns>
    private TelemetryEvent? ApplyContentGate(TelemetryEvent telemetryEvent, ServerCallContext context)
    {
        if (_contentGate is null) return telemetryEvent;

        switch (telemetryEvent.EventCase)
        {
            case TelemetryEvent.EventOneofCase.LogLine when telemetryEvent.LogLine.ContentBearing:
                return _contentGate.TryGetContentGrant(context) ? telemetryEvent : null;

            case TelemetryEvent.EventOneofCase.RoutingTelemetry
                when telemetryEvent.RoutingTelemetry.HasRequestSummary ||
                     telemetryEvent.RoutingTelemetry.HasResponseSummary:
                if (_contentGate.TryGetContentGrant(context)) return telemetryEvent;

                var redacted = telemetryEvent.Clone();
                redacted.RoutingTelemetry.ClearRequestSummary();
                redacted.RoutingTelemetry.ClearResponseSummary();
                return redacted;

            default:
                return telemetryEvent;
        }
    }

    /// <summary>
    /// Returns the most recent persisted <c>request_transcripts</c> rows for the GUI Sessions tab
    /// (docs/router/sessions-tab-training-data-plan.md Phase 1), shaped for display (#179, ADR-0023). Reads
    /// <see cref="TranscriptOptions.Enabled"/> itself, rather than trusting an empty transcript list to
    /// imply capture is off, so the response can tell the two states apart:
    /// <see cref="Contract.ListPersistedSessionsResponse.TranscriptCaptureEnabled"/> false means capture is
    /// off, not that no traffic has been persisted yet.
    /// </summary>
    /// <remarks>
    /// Three things keep the response under the GUI client's 4 MiB receive cap whatever the stored text:
    /// <list type="bullet">
    /// <item>The limit is clamped: 0 means <see cref="DefaultListLimit"/>, and anything else is clamped to
    /// [1, <see cref="MaxListLimit"/>].</item>
    /// <item>Each row carries <see cref="TextTruncator"/> previews of its texts, the same previews live
    /// telemetry carries, with truncation flags and the stored lengths.</item>
    /// <item>Rows are added newest first while the exact serialized size stays within
    /// <see cref="MaxListResponseBytes"/>, so the result is always a contiguous run of the newest rows.</item>
    /// </list>
    /// <see cref="Contract.ListPersistedSessionsResponse.HasMore"/> is set when the limit or the budget left
    /// older rows out. Only this display read is shortened; nothing written to the store changes.
    /// </remarks>
    public override async Task<ListPersistedSessionsResponse> ListPersistedSessions(
        ListPersistedSessionsRequest request,
        ServerCallContext context)
    {
        var enabled = _transcriptOptions.CurrentValue.Enabled;
        var response = new ListPersistedSessionsResponse { TranscriptCaptureEnabled = enabled };

        if (!enabled) return response;

        var limit = request.Limit == 0
            ? DefaultListLimit
            : Math.Clamp(value: request.Limit, min: 1, max: MaxListLimit);

        // One row past the limit: its presence alone says older rows exist.
        var transcripts = await _transcriptStore
            .ListSessionsAsync(limit: limit + 1, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        // Metadata only unless the caller holds a content grant (ADR-0020); one check covers every row.
        var includeText = _contentGate is null || _contentGate.TryGetContentGrant(context);

        var size = ListResponseFlagBytes;
        foreach (var transcript in transcripts.Take(limit))
        {
            var row = ToContract(transcript, includeText);
            // One tag byte for repeated field 2, then the length-prefixed row.
            var rowBytes = 1 + CodedOutputStream.ComputeMessageSize(row);
            if (size + rowBytes > MaxListResponseBytes)
            {
                response.HasMore = true;
                _logger.LogInformation(
                    "ListPersistedSessions returned {Returned} of {Requested} rows: the {BudgetBytes}-byte response budget was reached.",
                    response.Transcripts.Count, limit, MaxListResponseBytes);
                return response;
            }

            size += rowBytes;
            response.Transcripts.Add(row);
        }

        if (transcripts.Count > limit) response.HasMore = true;

        return response;
    }

    /// <summary>
    /// Maps one <see cref="SessionTranscript"/> onto its wire representation, with display previews in place
    /// of the stored texts (ADR-0023). With <paramref name="includeText"/> false (no content grant, ADR-0020)
    /// the prompt and response previews and their truncation flags are left unset; lengths stay, since they
    /// are metadata rather than content.
    /// </summary>
    private static PersistedTranscript ToContract(SessionTranscript transcript, bool includeText)
    {
        var contract = new PersistedTranscript
        {
            SessionId = transcript.SessionId,
            CorrelationId = transcript.CorrelationId,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(transcript.CreatedAtUtc),
            RequestedModel = transcript.RequestedModel,
            RoutedModel = transcript.RoutedModel,
            TranscriptId = transcript.Id
        };

        if (includeText && transcript.PromptText is { } promptText)
        {
            contract.PromptText = TextTruncator.Truncate(promptText)!;
            contract.PromptTruncated = promptText.Length > TextTruncator.DefaultMaxLength;
        }

        if (includeText && transcript.ResponseText is { } responseText)
        {
            contract.ResponseText = TextTruncator.Truncate(responseText)!;
            contract.ResponseTruncated = responseText.Length > TextTruncator.DefaultMaxLength;
        }

        if (transcript.PromptTextLength is { } promptTextLength) contract.PromptTextLength = promptTextLength;

        if (transcript.ResponseTextLength is { } responseTextLength)
            contract.ResponseTextLength = responseTextLength;

        if (transcript.Cost is { } cost) contract.CostUsd = cost.ToString(CultureInfo.InvariantCulture);

        if (transcript.InputTokens is { } inputTokens) contract.InputTokens = inputTokens;

        if (transcript.OutputTokens is { } outputTokens) contract.OutputTokens = outputTokens;

        if (transcript.MemoryEntryId is { } memoryEntryId) contract.MemoryEntryId = memoryEntryId;

        return contract;
    }
}
