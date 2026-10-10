using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <param name="pump">Moves the compression, encryption and disk writes off the relay path.</param>
/// <param name="writer">The background writer that stores finished turns.</param>
/// <param name="store">
/// The lazily opened session store. Opening it creates the index and runs startup recovery, which deletes every
/// spool no index row names, so it is opened before the first spool is created.
/// </param>
/// <param name="epoch">
/// The Clear epoch each capture begins under, so a turn that was in flight when Clear ran is dropped instead of
/// stored after it.
/// </param>
/// <param name="transcriptOptions">The live Transcription Capture toggle.</param>
/// <param name="captureOptions">The disk reserve every spool honours.</param>
/// <param name="database">Names where the session folder lives, beside <c>transcripts.db</c>.</param>
/// <param name="logger">Receives capture failures; message templates are static.</param>
/// <param name="onTurnDropped">
/// Marks a transcript row terminal when capture is dropped before the session file is written. Optional so
/// tests that build the factory by hand need no transcript store.
/// </param>
public sealed class TurnCaptureFactory(
    CaptureBodyPump pump,
    SessionCaptureWriter writer,
    Lazy<SessionStore> store,
    CaptureEpoch epoch,
    IOptionsMonitor<TranscriptOptions> transcriptOptions,
    IOptions<SessionCaptureOptions> captureOptions,
    TranscriptDatabase database,
    ILogger<TurnCaptureFactory> logger,
    Action<Guid>? onTurnDropped = null)
{
    /// <summary>The largest declared <c>Content-Length</c> used to presize the in-memory copy of a request.</summary>
    private const long MaxRequestBufferHint = 32L * 1024 * 1024;

    private readonly string _folder = SessionStore.FolderBeside(database.DatabasePath);

    /// <summary>
    /// Starts capturing a request, or declines when capture is off. Nothing touches the disk here: the
    /// request body is buffered in memory as the router reads it, and its spool is created only once a
    /// candidate answers, so rejected, cache-served and failed requests cost no file at all.
    /// </summary>
    /// <param name="context">The request about to be routed; its body is wrapped so reading it also captures it.</param>
    /// <returns>The capture to carry through the request, or <see langword="null"/> for none.</returns>
    internal TurnCapture? Begin(HttpContext context)
    {
        if (!transcriptOptions.CurrentValue.Enabled) return null;

        // A declared length sizes the in-memory copy up front, so a multi-megabyte history is not copied through
        // a chain of doubling buffers; a larger or unknown length starts empty and grows.
        var expectedBytes = context.Request.ContentLength is > 0 and <= MaxRequestBufferHint
            ? (int)context.Request.ContentLength.Value
            : 0;
        var capture = new TurnCapture(pump, writer, epoch, CreateSpool, logger, DateTimeOffset.UtcNow, expectedBytes,
            clientSessionId => store.Value.ResolveArchiveSessionId(clientSessionId), onTurnDropped);
        context.Request.Body = capture.TeeRequest(context.Request.Body);
        return capture;
    }

    /// <summary>
    /// Creates a spool in the session folder, so startup recovery sweeps any leftover. Runs on the pump's worker.
    /// The store is opened first: its recovery deletes every spool no index row names, so a store opened after a
    /// spool exists (capture switched on at run time, with nothing stored yet) would delete that spool and the
    /// turn it belongs to.
    /// </summary>
    /// <returns>A new spool.</returns>
    private SessionBodySpool CreateSpool()
    {
        _ = store.Value;
        return SessionBodySpool.Create(_folder, captureOptions.Value.MinFreeDiskBytes);
    }
}

/// <summary>
/// The capture of one proxied turn, carried from the moment the request body is first read to the moment the
/// finished turn is handed to <see cref="SessionCaptureWriter"/>. The client's request is buffered in memory
/// while the router reads it (it already holds the whole body) and starts its spool when a candidate answers;
/// the relayed response, and a translated turn's provider-side response, stream into spools through
/// <see cref="CaptureBodyPump"/>, so the relay never waits on the disk. If the turn is never submitted (the
/// request failed to resolve, the response was not committed), <see cref="Dispose"/> releases everything and
/// nothing is stored.
/// </summary>
/// <remarks>
/// A body is complete or absent, never a prefix (ADR-0019). A body whose spool was abandoned (disk reserve,
/// an unterminated secret match, an I/O error, a full pump queue), and a response whose relay was cut short
/// (the client left, or the upstream or a stream translator failed part-way), are recorded as missing. The reply
/// text in the extracts follows the same rule.
/// </remarks>
internal sealed class TurnCapture : IDisposable
{
    // Telemetry's own numbers can be non-finite (a propensity that is NaN); writing them as named literals keeps
    // one odd value from losing the whole turn, which is what a serializer exception would do.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>How long a finished turn waits for the pump and the writer before its bodies are given up.</summary>
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(30);

    private readonly CaptureBodyPump _pump;
    private readonly SessionCaptureWriter _writer;
    private readonly CaptureEpoch _epoch;
    private readonly long _startEpoch;
    private readonly Func<SessionBodySpool> _createSpool;
    private readonly ILogger _logger;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly List<SessionBodySpool> _ownedSpools = [];
    private MemoryStream? _requestBuffer;
    private CaptureBody? _request;
    private Task<SessionBodySpool?>? _requestCompletion;
    private CaptureBody? _response;
    private CaptureBody? _providerResponse;
    private TeeWriteStream? _clientTee;
    private byte[]? _providerRequest;
    private bool _interrupted;
    private bool _submitted;
    private readonly Func<string, Guid>? _resolveArchiveSession;
    private readonly Action<Guid>? _onTurnDropped;

    /// <summary>The turn id minted when capture began, shared with the transcript row.</summary>
    private Guid ArchiveTurnId { get; } = SessionArchiveIds.NewArchiveTurnId();

    /// <summary>
    /// Initializes a new instance of the <see cref="TurnCapture"/> class.
    /// </summary>
    /// <param name="pump">Applies every body's disk work off the relay path.</param>
    /// <param name="writer">Stores the finished turn.</param>
    /// <param name="epoch">The Clear epoch; the value now is the one this turn begins under.</param>
    /// <param name="createSpool">Creates a spool; called on the pump's worker.</param>
    /// <param name="logger">Receives failures; templates are static.</param>
    /// <param name="startedAtUtc">When the request arrived, recorded as the turn's creation time.</param>
    /// <param name="expectedRequestBytes">The request's declared length, to presize its in-memory copy; zero when unknown.</param>
    /// <param name="resolveArchiveSession">
    /// Resolves the session file id for a client session id. The same call the writer makes, so the transcript
    /// row and the file share one id. Null only for a capture that is never bound to a transcript row.
    /// </param>
    /// <param name="onTurnDropped">
    /// Marks the transcript row terminal when this capture is dropped before the writer stores it. Optional.
    /// </param>
    internal TurnCapture(
        CaptureBodyPump pump,
        SessionCaptureWriter writer,
        CaptureEpoch epoch,
        Func<SessionBodySpool> createSpool,
        ILogger logger,
        DateTimeOffset startedAtUtc,
        int expectedRequestBytes = 0,
        Func<string, Guid>? resolveArchiveSession = null,
        Action<Guid>? onTurnDropped = null)
    {
        _pump = pump;
        _writer = writer;
        _epoch = epoch;
        _startEpoch = epoch.Current;
        _createSpool = createSpool;
        _logger = logger;
        _startedAtUtc = startedAtUtc;
        _resolveArchiveSession = resolveArchiveSession;
        _onTurnDropped = onTurnDropped;
        _requestBuffer = new MemoryStream(Math.Max(0, expectedRequestBytes));
    }

    /// <summary>
    /// Resolves the session file id for <paramref name="clientSessionId"/> and returns the binding the
    /// transcript row must store. Uses the same pending-id map as the writer.
    /// </summary>
    /// <param name="clientSessionId">The client's session id.</param>
    /// <returns>The archive ids for this turn.</returns>
    internal ArchiveBinding BindArchive(string clientSessionId)
    {
        if (_resolveArchiveSession is null)
        {
            throw new InvalidOperationException("This capture has no session-id resolver.");
        }

        return new ArchiveBinding(_resolveArchiveSession(clientSessionId), ArchiveTurnId);
    }

    /// <summary>
    /// Wraps the client's request body so every byte the router reads is also copied into memory, exactly as
    /// sent and before it is decoded.
    /// </summary>
    /// <param name="body">The client's request body.</param>
    /// <returns>A read-through stream to install as the request body.</returns>
    internal Stream TeeRequest(Stream body) => new TeeReadStream(body, bytes => _requestBuffer?.Write(bytes));

    /// <summary>
    /// Records what is being sent to the provider for the candidate about to be attempted: the translated body
    /// when a translator rewrote the client's request, or <see langword="null"/> when the client's own request
    /// goes through unchanged. It is called for every attempt, so a failover candidate replaces an earlier one's
    /// (a pass-through candidate that serves after a translated one failed must not inherit its provider
    /// request), and whether the turn counts as translated is simply whether this holds a body.
    /// </summary>
    /// <param name="body">The provider-facing request body, or <see langword="null"/> when no translator ran.</param>
    internal void SetProviderRequest(byte[]? body) => _providerRequest = body;

    /// <summary>
    /// Starts capturing the response relayed to the client and returns the stream the writer must use in place
    /// of the client's response body. This is also the moment the request's spool starts, because a candidate
    /// is now answering. A failed-over candidate's bodies are released first, and a translated turn (one with a
    /// provider request) also captures the provider's response.
    /// </summary>
    /// <param name="clientBody">The client's response body.</param>
    /// <returns>A write-through stream that also feeds the response capture.</returns>
    internal Stream BeginResponse(Stream clientBody)
    {
        // The candidate that answers decides: an earlier candidate that failed over must not leave its
        // interruption or its provider response behind on a turn a later candidate served.
        _interrupted = false;
        _response?.Release();
        _providerResponse?.Release();
        _providerResponse = null;

        AttachRequest();
        _response = _pump.CreateBody(_createSpool);
        if (_providerRequest is not null) _providerResponse = _pump.CreateBody(_createSpool);

        _clientTee = new TeeWriteStream(clientBody, _response.Write);
        return _clientTee;
    }

    /// <summary>
    /// Wraps the upstream's response stream so reading it also captures the provider-side response, for a
    /// translated turn. Returns the stream unchanged when no provider capture exists.
    /// </summary>
    /// <param name="upstream">The upstream response body, before translation.</param>
    /// <returns>A read-through stream feeding the provider-response capture.</returns>
    internal Stream TeeProviderResponse(Stream upstream) =>
        _providerResponse is null ? upstream : new TeeReadStream(upstream, _providerResponse.Write);

    /// <summary>
    /// Appends bytes to the provider-side response when the router already has them in hand rather than
    /// reading them from a stream: an error body inspected for classification, or one Bedrock event payload.
    /// Does nothing for a turn with no provider-side response.
    /// </summary>
    /// <param name="body">The next bytes of the provider's response body.</param>
    internal void RecordProviderResponse(byte[] body) => _providerResponse?.Write(body);

    /// <summary>
    /// Records that the relay of the response did not run to its end (the client left, or the upstream or a
    /// stream translator failed part-way), so what was captured is a prefix and is stored as missing.
    /// </summary>
    internal void MarkInterrupted() => _interrupted = true;

    /// <summary>
    /// Ends the turn's bodies and hands the finished turn to the background writer, which then owns and
    /// disposes the spools. A body that cannot be finished before the deadline is recorded as missing and the
    /// stall is logged, and a turn the writer refuses is logged and dropped; neither fails the request.
    /// </summary>
    /// <param name="turn">What telemetry worked out for the turn.</param>
    /// <param name="context">The request, whose <c>User-Agent</c> is reduced to a harness token before it is stored.</param>
    /// <param name="telemetryCaptureTruncated">
    /// Whether telemetry's own capped copy of the response lost bytes (the client-shape or native copy hit its
    /// cap, or the tail scanner was needed). The reply text was extracted from that copy, so it is then left out
    /// of the extracts rather than stored cut off. A relay that did not run to its end is treated the same way:
    /// the copy then holds only what arrived before it stopped.
    /// </param>
    internal async Task SubmitAsync(PublishedTurn turn, HttpContext context, bool telemetryCaptureTruncated)
    {
        if (_submitted) return;

        // Clear ran while this turn was in flight. Its request holds the whole conversation so far, which Clear just
        // wiped, so storing it would write that history straight back; Dispose releases the bodies instead.
        if (_epoch.Current != _startEpoch)
        {
            _logger.LogInformation("A captured turn that began before Clear was dropped instead of stored.");
            NotifyTurnDropped();
            return;
        }

        // A short bound so a stalled pump or writer cannot hold this request's bookkeeping open.
        using var timeout = new CancellationTokenSource(SubmitTimeout);
        var cancellationToken = timeout.Token;

        try
        {
            // A write the client rejected leaves the relayed body a prefix, whichever side noticed first.
            var interrupted = _interrupted || _clientTee?.Faulted == true;
            if (interrupted)
            {
                _response?.Release();
                _providerResponse?.Release();
            }

            var requestSpool = await FinishAsync(_requestCompletion, cancellationToken).ConfigureAwait(false);
            var responseSpool = interrupted
                ? null
                : await FinishAsync(_response?.CompleteAsync(), cancellationToken).ConfigureAwait(false);
            var providerResponseSpool = interrupted
                ? null
                : await FinishAsync(_providerResponse?.CompleteAsync(), cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Captured bodies were not finished before the turn's deadline and are recorded as missing; the capture pump may be stalled.");
            }

            var bodies = new List<SessionBodyInput>(6)
            {
                new(SessionBodyKind.ClientRequest, null, requestSpool),
                new(SessionBodyKind.ClientResponse, null, responseSpool),
            };
            if (_providerRequest is not null)
            {
                bodies.Add(new SessionBodyInput(SessionBodyKind.ProviderRequest, _providerRequest));
                bodies.Add(new SessionBodyInput(SessionBodyKind.ProviderResponse, null, providerResponseSpool));
            }

            bodies.Add(new SessionBodyInput(
                SessionBodyKind.TurnMetadata, BuildMetadata(turn, context.Request.Headers.UserAgent.ToString())));
            bodies.Add(new SessionBodyInput(
                SessionBodyKind.Extracts, BuildExtracts(turn, telemetryCaptureTruncated || interrupted)));

            var item = new SessionCaptureItem(
                turn.SessionId,
                new SessionTurnInput(ArchiveTurnId, _startedAtUtc, bodies),
                _startEpoch);

            // From here the writer owns every spool, including when it refuses the turn (and marks the drop).
            _submitted = true;
            if (!await _writer.EnqueueAsync(item, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("A captured turn was refused by the session writer and dropped.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A captured turn could not be queued for session storage and was dropped.");
            NotifyTurnDropped();
        }
    }

    /// <summary>Releases everything the writer does not own. Safe to call after <see cref="SubmitAsync"/>.</summary>
    public void Dispose()
    {
        if (_submitted) return;

        NotifyTurnDropped();

        _requestBuffer?.Dispose();
        _requestBuffer = null;
        _request?.Release();
        _response?.Release();
        _providerResponse?.Release();
        foreach (var spool in _ownedSpools) spool.Dispose();
        _ownedSpools.Clear();

        // The request's completion was posted when the first candidate began answering, so by now the worker
        // has usually finished that spool and handed it to the completion task, out of Release's reach.
        DisposeWhenFinished(_requestCompletion);
    }

    /// <summary>Marks the transcript row terminal when this turn will never reach a session file.</summary>
    private void NotifyTurnDropped() => _onTurnDropped?.Invoke(ArchiveTurnId);

    /// <summary>
    /// Starts the request's spool from the bytes buffered while the router read the body, and ends it. A
    /// request that is attached once serves every later candidate.
    /// </summary>
    private void AttachRequest()
    {
        if (_request is not null || _requestBuffer is null) return;

        _request = _pump.CreateBody(_createSpool);
        _request.Write(_requestBuffer.GetBuffer().AsSpan(0, (int)_requestBuffer.Length));
        _requestCompletion = _request.CompleteAsync();
        _requestBuffer.Dispose();
        _requestBuffer = null;
    }

    /// <summary>
    /// Waits for a body's spool to be finished and takes ownership of it. A body that was dropped, or whose
    /// completion does not arrive before the turn's deadline, comes back as <see langword="null"/> and is
    /// recorded as missing; a spool that finishes after the deadline is disposed when it does.
    /// </summary>
    /// <param name="completion">The body's completion, or <see langword="null"/> when the body never started.</param>
    /// <param name="cancellationToken">The turn's deadline.</param>
    /// <returns>The finished spool, or <see langword="null"/>.</returns>
    private async Task<SessionBodySpool?> FinishAsync(Task<SessionBodySpool?>? completion, CancellationToken cancellationToken)
    {
        if (completion is null) return null;

        try
        {
            var spool = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (spool is not null) _ownedSpools.Add(spool);
            return spool;
        }
        catch (OperationCanceledException)
        {
            DisposeWhenFinished(completion);
            return null;
        }
    }

    /// <summary>
    /// Disposes the spool a body's completion yields, whenever it arrives, for a body nobody will store. The
    /// spool's own disposal is idempotent, so this is safe even when the spool was already taken and disposed.
    /// </summary>
    /// <param name="completion">The body's completion, or <see langword="null"/> when the body never started.</param>
    private static void DisposeWhenFinished(Task<SessionBodySpool?>? completion)
    {
        _ = completion?.ContinueWith(
            static finished =>
            {
                if (finished.IsCompletedSuccessfully) finished.Result?.Dispose();
            },
            TaskScheduler.Default);
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
            ["translated"] = _providerRequest is not null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
    }

    /// <summary>
    /// Builds the text extracts stored with the turn. The reply text is omitted, and flagged, when it was
    /// extracted from a copy that is only a prefix of the reply, because a cut-off reply must not be stored as
    /// if whole.
    /// </summary>
    /// <param name="turn">What telemetry worked out.</param>
    /// <param name="responseTruncated">
    /// Whether the reply text was extracted from a prefix: telemetry's capped copy lost bytes, or the relay
    /// stopped before the end.
    /// </param>
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
