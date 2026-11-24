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

        var size = ListResponseFlagBytes;
        foreach (var transcript in transcripts.Take(limit))
        {
            var row = ToContract(transcript);
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

    /// <summary>The serialized-page budget for <see cref="GetSessionTurnDetails"/>. The first row always fits.</summary>
    private const int MaxDetailsResponseBytes = 2 * 1024 * 1024;

    /// <summary>
    /// One newest-first metadata page for a session, and the retained-history aggregate on the first page.
    /// An empty session id is rejected. Capture off returns the flag and does not query.
    /// </summary>
    /// <param name="request">The session, the page size, and the cursor.</param>
    /// <param name="context">The call context.</param>
    /// <returns>The page. No conversation text.</returns>
    public override async Task<GetSessionTurnDetailsResponse> GetSessionTurnDetails(
        GetSessionTurnDetailsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "session_id is required."));
        }

        var enabled = _transcriptOptions.CurrentValue.Enabled;
        var response = new GetSessionTurnDetailsResponse { TranscriptCaptureEnabled = enabled };
        if (!enabled) return response;

        var limit = request.Limit == 0 ? DefaultListLimit : Math.Clamp(request.Limit, 1, 2000);
        long? before = request.HasBeforeTranscriptId ? request.BeforeTranscriptId : null;
        var rows = await _transcriptStore.ListSessionTurnMetadataAsync(
            request.SessionId, limit + 1, before, context.CancellationToken).ConfigureAwait(false);

        var size = 0;
        foreach (var row in rows.Take(limit))
        {
            var detail = ToDetail(row);
            var rowBytes = 1 + CodedOutputStream.ComputeMessageSize(detail);
            if (response.Turns.Count > 0 && size + rowBytes > MaxDetailsResponseBytes) break;
            size += rowBytes;
            response.Turns.Add(detail);
        }

        if (rows.Count > response.Turns.Count && response.Turns.Count > 0)
            response.NextBeforeTranscriptId = response.Turns[^1].TranscriptId;

        if (!request.HasBeforeTranscriptId)
        {
            var aggregate = await _transcriptStore.GetSessionAggregateAsync(
                request.SessionId, request.LiveFloorTurnNumber, context.CancellationToken).ConfigureAwait(false);
            if (aggregate is not null) response.Aggregate = ToAggregate(aggregate);
        }

        return response;
    }

    private static SessionTurnDetail ToDetail(SessionTurnMetadata row)
    {
        var detail = new SessionTurnDetail
        {
            TranscriptId = row.TranscriptId,
            TurnNumber = row.TurnNumber,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(row.CreatedAtUtc),
            RequestedModel = row.RequestedModel,
            RoutedModel = row.RoutedModel,
            IsUtility = row.IsUtility,
            IsExploratory = row.IsExploratory,
            Propensity = row.Propensity,
            IsJudgeScored = row.IsJudgeScored,
        };
        if (row.Dimension is not null) detail.Dimension = row.Dimension;
        if (row.Difficulty is not null) detail.Difficulty = row.Difficulty;
        if (row.Language is not null) detail.Language = row.Language;
        if (row.Score is not null) detail.Score = row.Score.Value;
        if (row.CostUsd is not null) detail.CostUsd = row.CostUsd.Value.ToString(CultureInfo.InvariantCulture);
        if (row.InputTokens is not null) detail.InputTokens = row.InputTokens.Value;
        if (row.OutputTokens is not null) detail.OutputTokens = row.OutputTokens.Value;
        if (row.MemoryEntryId is not null) detail.MemoryEntryId = row.MemoryEntryId.Value;
        if (row.DimBestModel is not null) detail.DimBestModel = row.DimBestModel;
        if (row.ScorerVersion is not null) detail.ScorerVersion = row.ScorerVersion;
        if (row.UntrainedBaselineModel is not null) detail.UntrainedBaselineModel = row.UntrainedBaselineModel;
        if (row.UntrainedBaselinePredictedScore is not null)
            detail.UntrainedBaselinePredictedScore = row.UntrainedBaselinePredictedScore.Value;
        if (row.PromptTextLength is not null) detail.PromptTextLength = row.PromptTextLength.Value;
        if (row.ResponseTextLength is not null) detail.ResponseTextLength = row.ResponseTextLength.Value;
        if (row.HasToolCalls is not null) detail.HasToolCalls = row.HasToolCalls.Value;
        if (row.Comparison is { } comparison) detail.Comparison = ToComparison(comparison);
        return detail;
    }

    private static BaselineComparison ToComparison(SessionBaselineComparison row)
    {
        var message = new BaselineComparison
        {
            ComparedAtUtc = Timestamp.FromDateTimeOffset(row.ComparedAtUtc),
            ObservedScore = row.ObservedScore,
            IsClustered = row.IsClustered,
        };
        if (row.DimensionPredictedScore is not null) message.DimensionPredictedScore = row.DimensionPredictedScore.Value;
        if (row.ClusterPredictedScore is not null) message.ClusterPredictedScore = row.ClusterPredictedScore.Value;
        if (row.DimensionAbsError is not null) message.DimensionAbsError = row.DimensionAbsError.Value;
        if (row.ClusterAbsError is not null) message.ClusterAbsError = row.ClusterAbsError.Value;
        if (row.BaselineModel is not null) message.BaselineModel = row.BaselineModel;
        if (row.ActualCostUsd is not null) message.ActualCostUsd = Money(row.ActualCostUsd.Value);
        if (row.BaselineEstimatedCostUsd is not null) message.BaselineEstimatedCostUsd = Money(row.BaselineEstimatedCostUsd.Value);
        if (row.EstimatedNetSavingsUsd is not null) message.EstimatedNetSavingsUsd = Money(row.EstimatedNetSavingsUsd.Value);
        if (row.BaselinePredictedScore is not null) message.BaselinePredictedScore = row.BaselinePredictedScore.Value;
        if (row.EstimatedRegret is not null) message.EstimatedRegret = row.EstimatedRegret.Value;
        if (row.BaselineTokenizerRatio is not null) message.BaselineTokenizerRatio = row.BaselineTokenizerRatio.Value;
        if (row.BaselineTokenizerRatioMeasured is not null)
            message.BaselineTokenizerRatioMeasured = row.BaselineTokenizerRatioMeasured.Value;
        if (row.BaselineInputTokens is not null) message.BaselineInputTokens = row.BaselineInputTokens.Value;
        if (row.BaselineOutputTokens is not null) message.BaselineOutputTokens = row.BaselineOutputTokens.Value;
        if (row.BaselineInputPricePerMillion is not null)
            message.BaselineInputPricePerMillion = Money(row.BaselineInputPricePerMillion.Value);
        if (row.BaselineOutputPricePerMillion is not null)
            message.BaselineOutputPricePerMillion = Money(row.BaselineOutputPricePerMillion.Value);
        return message;
    }

    private static Contract.SessionAggregate ToAggregate(Transcripts.SessionAggregate row)
    {
        var message = new Contract.SessionAggregate
        {
            PersistedTurns = row.PersistedTurns,
            TrainedTurns = row.TrainedTurns,
            ComparedTurns = row.ComparedTurns,
            PersistedTurnNumbersComplete = row.PersistedTurnNumbersComplete,
            UnpricedTurns = row.UnpricedTurns,
            TotalInputTokens = row.TotalInputTokens,
            TotalOutputTokens = row.TotalOutputTokens,
            TokenUnknownTurns = row.TokenUnknownTurns,
            OtherModels = row.OtherModels,
            OtherModelTurns = row.OtherModelTurns,
        };
        if (row.TotalEstimatedNetSavingsUsd is not null)
            message.TotalEstimatedNetSavingsUsd = Money(row.TotalEstimatedNetSavingsUsd.Value);
        if (row.TotalBaselineEstimatedCostUsd is not null)
            message.TotalBaselineEstimatedCostUsd = Money(row.TotalBaselineEstimatedCostUsd.Value);
        if (row.FirstTurnUtc is not null) message.FirstTurnUtc = Timestamp.FromDateTimeOffset(row.FirstTurnUtc.Value);
        if (row.LastTurnUtc is not null) message.LastTurnUtc = Timestamp.FromDateTimeOffset(row.LastTurnUtc.Value);
        if (row.TotalCostUsd is not null) message.TotalCostUsd = Money(row.TotalCostUsd.Value);
        message.PersistedTurnNumbersFromFloor.AddRange(row.PersistedTurnNumbersFromFloor);
        foreach (var model in row.Models)
        {
            var usage = new Contract.SessionModelUsage
            {
                RoutedModel = model.RoutedModel,
                Turns = model.Turns,
                UnpricedTurns = model.UnpricedTurns,
                LastUsedUtc = Timestamp.FromDateTimeOffset(model.LastUsedUtc),
            };
            if (model.CostUsd is not null) usage.CostUsd = Money(model.CostUsd.Value);
            message.Models.Add(usage);
        }

        return message;
    }

    private static string Money(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The most transcript ids one <see cref="GetTurnTexts"/> call accepts.</summary>
    private const int MaxTurnTextIds = 50;

    /// <inheritdoc />
    public override async Task<GetTurnTextsResponse> GetTurnTexts(GetTurnTextsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_contentGate is not null) ContentGateHooks.RequireTurnTexts(_contentGate, context);

        if (request.TranscriptIds.Count is 0 or > MaxTurnTextIds)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "GetTurnTexts takes between 1 and 50 transcript ids."));
        }

        var response = new GetTurnTextsResponse
        {
            TranscriptCaptureEnabled = _transcriptOptions.CurrentValue.Enabled
        };
        if (!response.TranscriptCaptureEnabled) return response;

        var texts = await _transcriptStore
            .LoadTurnTextsAsync(request.TranscriptIds, context.CancellationToken)
            .ConfigureAwait(false);

        foreach (var text in texts)
        {
            var row = new TurnText { TranscriptId = text.TranscriptId, Found = text.Found };
            if (text.PromptText is { } prompt)
            {
                row.PromptText = TextTruncator.Truncate(prompt)!;
                row.PromptTruncated = prompt.Length > TextTruncator.DefaultMaxLength;
            }

            if (text.ResponseText is { } reply)
            {
                row.ResponseText = TextTruncator.Truncate(reply)!;
                row.ResponseTruncated = reply.Length > TextTruncator.DefaultMaxLength;
            }

            if (text.PromptTextLength is { } promptLength) row.PromptTextLength = promptLength;
            if (text.ResponseTextLength is { } responseLength) row.ResponseTextLength = responseLength;
            response.Texts.Add(row);
        }

        return response;
    }

    /// <summary>
    /// Maps one <see cref="SessionTranscript"/> onto its wire representation. The list carries lengths and
    /// other metadata only (#165 phase 2). Opening a chat loads previews through <see cref="GetTurnTexts"/>.
    /// </summary>
    private static PersistedTranscript ToContract(SessionTranscript transcript)
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
