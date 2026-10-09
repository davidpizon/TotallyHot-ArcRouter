using System.Text.Json;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Sessions;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Decides whether a request is captured (#165, ADR-0019) and starts its <see cref="TurnCapture"/>. Capture
/// follows only the Transcription Capture toggle (<see cref="TranscriptOptions.Enabled"/>, read live so a
/// toggle takes effect on the next request without a restart); it no longer also requires Adaptive Routing.
/// When the toggle is off, <see cref="Begin"/> returns <see langword="null"/> and the hot path makes no second
/// copy of any body.
/// </summary>
/// <param name="writer">The background writer that stores finished turns.</param>
/// <param name="transcriptOptions">The live Transcription Capture toggle.</param>
/// <param name="captureOptions">The disk reserve every spool honours.</param>
/// <param name="database">Names where the session folder lives, beside <c>transcripts.db</c>.</param>
/// <param name="logger">Receives capture failures; message templates are static.</param>
public sealed class TurnCaptureFactory(
    SessionCaptureWriter writer,
    IOptionsMonitor<TranscriptOptions> transcriptOptions,
    IOptions<SessionCaptureOptions> captureOptions,
    TranscriptDatabase database,
    ILogger<TurnCaptureFactory> logger)
{
    private readonly string _folder = SessionStore.FolderBeside(database.DatabasePath);

    /// <summary>
    /// Starts capturing a request, or declines when capture is off or cannot start. A failure to start is
    /// logged and never fails the request: the turn is simply not captured.
    /// </summary>
    /// <param name="context">The request about to be routed; its body is wrapped so reading it also captures it.</param>
    /// <returns>The capture to carry through the request, or <see langword="null"/> for none.</returns>
    internal TurnCapture? Begin(HttpContext context)
    {
        if (!transcriptOptions.CurrentValue.Enabled) return null;

        SessionBodySpool? requestSpool = null;
        try
        {
            requestSpool = SessionBodySpool.Create(_folder, captureOptions.Value.MinFreeDiskBytes);
            var capture = new TurnCapture(
                writer, _folder, captureOptions.Value.MinFreeDiskBytes, logger, requestSpool, DateTimeOffset.UtcNow);
            context.Request.Body = new SpoolTeeReadStream(context.Request.Body, requestSpool);
            return capture;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            requestSpool?.Dispose();
            logger.LogWarning(ex, "Session capture could not start for a request; it will not be captured.");
            return null;
        }
    }
}

/// <summary>
/// The capture of one proxied turn, carried from the moment the request body is first read to the moment the
/// finished turn is handed to <see cref="SessionCaptureWriter"/>. It owns the spools that hold the client's
/// request and the relayed response (and the provider-side pair when a translator ran) until they are
/// submitted; if the turn is never submitted (the request failed to resolve, the response was not committed, a
/// Bedrock candidate answered), <see cref="Dispose"/> deletes the spools and nothing is stored.
/// </summary>
/// <remarks>
/// A body is complete or absent, never a prefix (ADR-0019). A spool that was abandoned (disk reserve, an
/// unterminated secret match, an I/O error) or whose relay was cut off by the client leaving is recorded as a
/// missing body.
/// </remarks>
internal sealed class TurnCapture : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>How long a finished turn waits for queue space before it is dropped.</summary>
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(30);

    private readonly SessionCaptureWriter _writer;
    private readonly string _folder;
    private readonly long _minFreeBytes;
    private readonly ILogger _logger;
    private readonly SessionBodySpool _request;
    private readonly DateTimeOffset _startedAtUtc;
    private SessionBodySpool? _response;
    private SessionBodySpool? _providerResponse;
    private byte[]? _providerRequest;
    private bool _translated;
    private bool _submitted;

    /// <summary>
    /// Initializes a new instance of the <see cref="TurnCapture"/> class.
    /// </summary>
    /// <param name="writer">Stores the finished turn.</param>
    /// <param name="folder">Where spools are created: the session folder, so startup recovery sweeps leftovers.</param>
    /// <param name="minFreeBytes">The disk reserve each spool honours.</param>
    /// <param name="logger">Receives failures; templates are static.</param>
    /// <param name="request">The spool already receiving the client's request body.</param>
    /// <param name="startedAtUtc">When the request arrived, recorded as the turn's creation time.</param>
    internal TurnCapture(
        SessionCaptureWriter writer,
        string folder,
        long minFreeBytes,
        ILogger logger,
        SessionBodySpool request,
        DateTimeOffset startedAtUtc)
    {
        _writer = writer;
        _folder = folder;
        _minFreeBytes = minFreeBytes;
        _logger = logger;
        _request = request;
        _startedAtUtc = startedAtUtc;
    }

    /// <summary>
    /// Ends the request body once the router has read it to the end, so the spool can be committed. Call it
    /// after <c>RequestInterceptor</c> has consumed the body.
    /// </summary>
    internal void CompleteRequest() => _request.TryComplete();

    /// <summary>
    /// Remembers the body sent to the provider when a translator rewrote the client's request. It is already
    /// in memory (the forwarded <see cref="ByteArrayContent"/>), so it is stored through the in-memory path.
    /// A later failover candidate replaces an earlier one's, and <see cref="BeginResponse"/> decides whether the
    /// candidate that actually answered was translated at all.
    /// </summary>
    /// <param name="body">The provider-facing request body.</param>
    internal void SetProviderRequest(byte[] body)
    {
        _translated = true;
        _providerRequest = body;
    }

    /// <summary>
    /// Starts capturing the response relayed to the client and returns the stream the writer must use in place
    /// of the client's response body. If the spool cannot be created the original stream is returned and the
    /// response is recorded as missing.
    /// </summary>
    /// <param name="clientBody">The client's response body.</param>
    /// <param name="translated">Whether a translator ran, which makes the provider's response a separate body.</param>
    /// <returns>A write-through stream that also feeds the response spool.</returns>
    internal Stream BeginResponse(Stream clientBody, bool translated)
    {
        // The candidate that answers decides: an earlier translated candidate that failed over must not leave
        // its provider request behind on a turn a pass-through candidate served.
        _translated = translated;
        if (!translated) _providerRequest = null;

        // A candidate that began relaying and then failed over leaves its spools behind; release them first.
        _response?.Dispose();
        _providerResponse?.Dispose();
        _providerResponse = null;
        _response = TryCreateSpool();
        if (translated) _providerResponse = TryCreateSpool();
        return _response is null ? clientBody : new SpoolTeeWriteStream(clientBody, _response);
    }

    /// <summary>
    /// Wraps the upstream's response stream so reading it also captures the provider-side response, for a
    /// translated turn. Returns the stream unchanged when no provider spool exists.
    /// </summary>
    /// <param name="upstream">The upstream response body, before translation.</param>
    /// <returns>A read-through stream feeding the provider-response spool.</returns>
    internal Stream TeeProviderResponse(Stream upstream) =>
        _providerResponse is null ? upstream : new SpoolTeeReadStream(upstream, _providerResponse);

    /// <summary>
    /// Appends bytes to the provider-side response when the router already has them in hand rather than
    /// reading them from a stream: an error body inspected for classification, or one Bedrock event payload.
    /// </summary>
    /// <param name="body">The next bytes of the provider's response body.</param>
    internal void RecordProviderResponse(byte[] body) => _providerResponse?.TryWrite(body);

    /// <summary>
    /// Ends the response bodies. A relay cut short by the client leaving leaves a prefix, which is recorded as
    /// missing rather than stored.
    /// </summary>
    /// <param name="relayInterrupted">Whether the client aborted before the response finished.</param>
    internal void CompleteResponse(bool relayInterrupted)
    {
        foreach (var spool in new[] { _response, _providerResponse })
        {
            if (spool is null) continue;
            if (relayInterrupted) spool.Abandon();
            else spool.TryComplete();
        }
    }

    /// <summary>
    /// Hands the finished turn to the background writer, which then owns and disposes the spools. A turn the
    /// writer refuses is dropped and logged; it never fails the request.
    /// </summary>
    /// <param name="turn">What telemetry worked out for the turn.</param>
    /// <param name="context">The request, whose <c>User-Agent</c> is reduced to a harness token before it is stored.</param>
    /// <param name="telemetryCapturedBytes">
    /// How many response bytes telemetry kept. A count at the 4 MiB cap means the reply text was extracted from a
    /// prefix, so it is left out of the extracts rather than stored cut off.
    /// </param>
    internal async Task SubmitAsync(PublishedTurn turn, HttpContext context, int telemetryCapturedBytes)
    {
        if (_submitted) return;

        // A short bound so a stalled writer cannot hold this request's bookkeeping open.
        using var timeout = new CancellationTokenSource(SubmitTimeout);
        var cancellationToken = timeout.Token;
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var responseTruncatedForExtraction = telemetryCapturedBytes >= UpstreamResponseWriter.MaxCapturedResponseBytes;

        try
        {
            var bodies = new List<SessionBodyInput>(6)
            {
                new(SessionBodyKind.ClientRequest, null, _request),
                new(SessionBodyKind.ClientResponse, null, _response),
            };
            if (_translated)
            {
                bodies.Add(new SessionBodyInput(SessionBodyKind.ProviderRequest, _providerRequest));
                bodies.Add(new SessionBodyInput(SessionBodyKind.ProviderResponse, null, _providerResponse));
            }

            bodies.Add(new SessionBodyInput(SessionBodyKind.TurnMetadata, BuildMetadata(turn, userAgent)));
            bodies.Add(new SessionBodyInput(SessionBodyKind.Extracts, BuildExtracts(turn, responseTruncatedForExtraction)));

            var item = new SessionCaptureItem(
                turn.SessionId,
                new SessionTurnInput(SessionArchiveIds.NewArchiveTurnId(), _startedAtUtc, bodies));

            // From here the writer owns every spool, including when it refuses the turn.
            _submitted = true;
            await _writer.EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A captured turn could not be queued for session storage and was dropped.");
        }
    }

    /// <summary>Deletes any spool the writer does not own. Safe to call after <see cref="SubmitAsync"/>.</summary>
    public void Dispose()
    {
        if (_submitted) return;

        _request.Dispose();
        _response?.Dispose();
        _providerResponse?.Dispose();
    }

    /// <summary>
    /// Creates a spool in the session folder, or returns <see langword="null"/> (logged) so the body is
    /// recorded as missing instead of failing the response.
    /// </summary>
    /// <returns>A new spool, or <see langword="null"/> when one could not be created.</returns>
    private SessionBodySpool? TryCreateSpool()
    {
        try
        {
            return SessionBodySpool.Create(_folder, _minFreeBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Session capture could not create a spool; that body will be recorded as missing.");
            return null;
        }
    }

    /// <summary>
    /// Builds the metadata snapshot stored with the turn: the routing facts as they were when computed, so an
    /// export never joins a store with its own retention.
    /// </summary>
    /// <param name="turn">What telemetry worked out.</param>
    /// <param name="userAgent">The raw header, reduced here to a harness token; the header itself is never stored.</param>
    /// <returns>The snapshot as UTF-8 JSON.</returns>
    private byte[] BuildMetadata(PublishedTurn turn, string? userAgent)
    {
        var snapshot = new Dictionary<string, object?>
        {
            ["session_id"] = turn.SessionId,
            ["turn_number"] = turn.TurnNumber,
            ["session_synthesized"] = turn.IsSessionSynthesized,
            ["correlation_id"] = turn.CorrelationId,
            ["created_at_utc"] = _startedAtUtc,
            ["origin"] = SessionTurnOrigin.Captured,
            ["harness"] = HarnessToken.Normalize(userAgent),
            ["provider"] = turn.Provider,
            ["requested_model"] = turn.RequestedModel,
            ["routed_model"] = turn.RoutedModel,
            ["resolved_provider_model_id"] = turn.ProviderModelId,
            ["substitution_reason"] = turn.SubstitutionReason.ToString(),
            ["fallback"] = turn.IsFallback,
            ["exploratory"] = turn.IsExploratory,
            ["propensity"] = turn.Propensity,
            ["dimension"] = turn.Classification?.Dimension,
            ["difficulty"] = turn.Classification?.Difficulty,
            ["language"] = turn.Classification?.Language,
            ["utility"] = turn.Classification?.IsUtility,
            ["subagent"] = turn.Classification?.Subagent?.ToLabel(),
            ["prompt_tokens"] = turn.PromptTokens,
            ["completion_tokens"] = turn.CompletionTokens,
            ["cache_creation_tokens"] = turn.CacheCreationTokens,
            ["cache_read_tokens"] = turn.CacheReadTokens,
            ["estimated_cost_usd"] = turn.EstimatedCostUsd,
            ["cost_confidence"] = turn.CostConfidence.ToString(),
            ["status_code"] = turn.StatusCode,
            ["latency_to_headers_ms"] = turn.LatencyToHeadersMs,
            ["total_duration_ms"] = turn.TotalDurationMs,
            ["streaming"] = turn.IsStreaming,
            ["content_encoding"] = turn.IsStreaming ? "sse" : "json",
            ["translated"] = _translated,
        };
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
    }

    /// <summary>
    /// Builds the text extracts stored with the turn. The reply text is omitted, and flagged, when it was
    /// extracted from a capture that hit its cap, because a cut-off reply must not be stored as if whole.
    /// </summary>
    /// <param name="turn">What telemetry worked out.</param>
    /// <param name="responseTruncated">Whether the telemetry copy of the response was cut at its cap.</param>
    /// <returns>The extracts as UTF-8 JSON.</returns>
    private static byte[] BuildExtracts(PublishedTurn turn, bool responseTruncated)
    {
        var extracts = new Dictionary<string, object?>
        {
            ["newest_user_message"] = turn.NewestUserMessage,
            ["response_text"] = responseTruncated ? null : turn.ResponseText,
            ["response_text_truncated"] = responseTruncated,
        };
        return JsonSerializer.SerializeToUtf8Bytes(extracts, JsonOptions);
    }
}
