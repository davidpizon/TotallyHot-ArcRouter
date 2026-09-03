using System.Buffers;
using System.Text.Json;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// The ArcRouter-authored response headers written alongside every committed upstream response, so a
/// client can see what it asked for versus what actually served it
/// (<c>docs/router/orchestrator-live-path-plan.md</c> §M2.2). Grouped into one record rather than three
/// parameters because they are always set together, immediately before the first body byte, and are
/// meaningless individually.
/// </summary>
/// <param name="RequestedModel">The model name the client literally asked for.</param>
/// <param name="RoutedModel">The model that actually served the request.</param>
/// <param name="SubstitutionReason">Why they differ, or <c>None</c>.</param>
internal readonly record struct RoutingResponseHeaders(
    string RequestedModel,
    string RoutedModel,
    string SubstitutionReason);

/// <summary>
/// The outcome of committing one upstream response to the client.
/// </summary>
/// <param name="Committed">
/// <see langword="false"/> when the response could not be forwarded and an error envelope was written
/// instead - the caller must return immediately rather than publishing telemetry for a forward that
/// never happened. Only the buffered-translation path can produce this, since it is the one path that
/// can fail before any byte reaches the client.
/// </param>
/// <param name="CapturedResponseBytes">A capped copy of what actually reached the client, for telemetry's usage/text parsers.</param>
/// <param name="NativeResponseBytes">A capped copy of the pre-translation upstream body, for providers whose native shape telemetry can read directly; otherwise <see langword="null"/>.</param>
/// <param name="TailScanner">The trailing-window usage scanner, when one was allocated.</param>
/// <param name="IsStreaming">Whether the upstream answered with <c>text/event-stream</c>.</param>
internal readonly record struct UpstreamResponseResult(
    bool Committed,
    byte[] CapturedResponseBytes,
    byte[]? NativeResponseBytes,
    IncrementalUsageScanner? TailScanner,
    bool IsStreaming);

/// <summary>
/// Commits one upstream response to the client: copies the forwardable headers, decides between the
/// three body paths (a decoded embedded error, a raw pre-read error body, or a live copy/translation of
/// the upstream stream), and captures a capped copy of what was sent for telemetry.
///
/// <para>
/// Extracted from <see cref="ProxyMiddleware.InvokeCoreAsync"/>'s candidate loop together with the
/// capture machinery it drives (<see cref="CopyAndCaptureAsync"/>,
/// <see cref="TranslateAndCaptureStreamAsync"/>, <see cref="TranslateAndCaptureBufferedAsync"/> and
/// their shared accumulator), which had no callers outside this concern. Moving them here is what keeps
/// the extraction from being cosmetic: the streaming/capture code is a self-contained subsystem with one
/// dependency (a logger), not part of the middleware's request-routing job.
/// </para>
/// </summary>
/// <param name="logger">Used only to report interrupted or truncated forwards; every capture path fails open rather than throwing.</param>
internal sealed class UpstreamResponseWriter(ILogger logger)
{
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Writes the committed response and returns what reached the client.
    /// </summary>
    /// <param name="context">The client request/response being served.</param>
    /// <param name="responseMessage">The upstream response. Not disposed here - the caller owns it.</param>
    /// <param name="translator">The provider's translator, or <see langword="null"/> for a passthrough provider.</param>
    /// <param name="routingHeaders">The ArcRouter-authored headers to set before the first body byte.</param>
    /// <param name="preReadErrorBody">The error body already buffered during classification, when one was; otherwise <see langword="null"/>.</param>
    /// <param name="embeddedErrorMessage">The translator-decoded error message, when one was extracted.</param>
    /// <param name="statusCode">The upstream status, used as the <c>code</c> of a synthesized error envelope.</param>
    internal async Task<UpstreamResponseResult> WriteAsync(
        HttpContext context,
        HttpResponseMessage responseMessage,
        IPayloadTranslator? translator,
        RoutingResponseHeaders routingHeaders,
        byte[]? preReadErrorBody,
        string? embeddedErrorMessage,
        int statusCode)
    {
        var isStreaming = CopyStatusAndHeaders(context, responseMessage, translator, routingHeaders, statusCode);

        if (preReadErrorBody is not null && embeddedErrorMessage is not null)
        {
            // An error whose body actually contained a decodable envelope. The body was already read to
            // make that determination; running it through TranslateResponse or the stream translator now
            // would mangle it into a bogus empty completion or (for the SSE shape) silently swallow it, so
            // a clean OpenAI-shaped error is written directly instead, preserving whatever message the
            // provider actually sent.
            context.Response.ContentType = "application/json";
            context.Response.Headers.Remove("Content-Length");
            var errorPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                error = new
                {
                    message = embeddedErrorMessage,
                    type = "invalid_request_error",
                    param = (string?)null,
                    code = statusCode.ToString(),
                },
            });
            await context.Response.Body.WriteAsync(errorPayload, context.RequestAborted);
            return new UpstreamResponseResult(true, errorPayload, null, null, isStreaming);
        }

        if (preReadErrorBody is not null)
        {
            // Pre-read, but no recognizable embedded error object - per TryExtractEmbeddedError's contract,
            // forward the raw body unchanged rather than losing it behind a synthetic generic message.
            context.Response.Headers.Remove("Content-Length");
            await context.Response.Body.WriteAsync(preReadErrorBody, context.RequestAborted);
            return new UpstreamResponseResult(true, preReadErrorBody, null, null, isStreaming);
        }

        using var upstreamBody = await responseMessage.Content.ReadAsStreamAsync(context.RequestAborted);
        try
        {
            if (translator is null)
            {
                var (captured, tailScanner) = await CopyAndCaptureAsync(
                    upstreamBody, context.Response.Body, MaxCapturedResponseBytes, context.RequestAborted);
                return new UpstreamResponseResult(true, captured, null, tailScanner, isStreaming);
            }

            var translated = isStreaming
                ? await TranslateAndCaptureStreamAsync(translator, upstreamBody, context.Response.Body, MaxCapturedResponseBytes, context.RequestAborted)
                : await TranslateAndCaptureBufferedAsync(translator, upstreamBody, context.Response.Body, MaxCapturedResponseBytes, context.RequestAborted);

            return new UpstreamResponseResult(
                true,
                translated.ClientShapeBytes,
                translated.NativeBytes,
                translated.TailScanner,
                isStreaming);
        }
        catch (Exception ex) when (!context.Response.HasStarted && ProxyMiddleware.IsStreamAbort(ex))
        {
            // Only TranslateAndCaptureBufferedAsync can reach here: it's the one dispatch path above that
            // can fail before writing anything (it buffers the whole upstream body before the one
            // translated write), and it only rethrows a non-client-abort I/O failure (see its own catch).
            // CopyAndCaptureAsync/TranslateAndCaptureStreamAsync always fail open internally instead of
            // throwing, since they write incrementally and so have necessarily already started the response
            // by the time anything could go wrong. Nothing has been committed yet, so this can still become
            // a clean 502 instead of silently returning a 200 with an empty body.
            _logger.LogWarning(ex, "Buffered upstream read failed before any response bytes were sent to the client; reporting an upstream error instead of an empty success.");
            await ProxyMiddleware.WriteUpstreamErrorResponseAsync(context, "The upstream provider closed the connection unexpectedly.");
            return new UpstreamResponseResult(false, [], null, null, isStreaming);
        }
    }

    /// <summary>
    /// Relays the upstream's status code, copies its forwardable headers onto the client response, drops
    /// the ones a translated body invalidates, and stamps ArcRouter's own routing headers. Returns whether
    /// the upstream answered with an SSE stream, which decides the body path.
    /// </summary>
    private static bool CopyStatusAndHeaders(
        HttpContext context,
        HttpResponseMessage responseMessage,
        IPayloadTranslator? translator,
        RoutingResponseHeaders routingHeaders,
        int statusCode)
    {
        var responseHopByHopHeaders = ProxyMiddleware.GetHopByHopHeaderNames(responseMessage.Headers.Connection);

        // The client sees the upstream's own status - this hop was chosen to answer, so a 400 stays a 400
        // rather than becoming an ArcRouter-authored error.
        context.Response.StatusCode = statusCode;

        foreach (var header in responseMessage.Headers)
        {
            if (responseHopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in responseMessage.Content.Headers)
        {
            if (responseHopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        var isStreaming = string.Equals(
            responseMessage.Content.Headers.ContentType?.MediaType,
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase);

        // A translated body no longer matches the upstream's own Content-Length (or, for streaming, its
        // Content-Type framing is re-emitted by us): drop the copied Content-Length so Kestrel sizes the
        // rewritten body itself rather than truncating it against a stale length. Content-Encoding is
        // dropped for the same reason and belt-and-suspenders alongside skipping the client's own
        // Accept-Encoding on the way upstream (see UpstreamRequestBuilder.AlwaysSkippedRequestHeaders): the
        // translator always writes fresh, uncompressed UTF-8 text, so a copied "Content-Encoding: gzip" (or
        // any other value) would be a lie the client then fails to decode.
        if (translator is not null)
        {
            context.Response.Headers.Remove("Content-Length");
            context.Response.Headers.Remove("Content-Encoding");
        }

        // docs/router/orchestrator-live-path-plan.md §M2.2: requested-vs-routed surfaced in response
        // headers (not the provider-shaped JSON body) so it works identically for streaming and buffered
        // responses. Set before any body byte is written, alongside the rest of this hop's headers above.
        context.Response.Headers[ProxyMiddleware.RequestedModelHeaderName] = routingHeaders.RequestedModel;
        context.Response.Headers[ProxyMiddleware.RoutedModelHeaderName] = routingHeaders.RoutedModel;
        context.Response.Headers[ProxyMiddleware.SubstitutionReasonHeaderName] = routingHeaders.SubstitutionReason;

        return isStreaming;
    }

    // Cap on how much of the response body telemetry captures for usage parsing (see CopyAndCaptureAsync).
    // Real chat/completion responses are almost always well under this; a response that exceeds it just
    // means usage parsing has less to work with (a truncated/partial buffer that the usage parsers already
    // handle gracefully by finding nothing), never a failure of the actual client-facing forward, which is
    // unaffected by this cap - every byte is still copied to the client regardless.
    internal const int MaxCapturedResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The result of capturing a translated response for telemetry: the OpenAI-shaped bytes actually sent
    /// to the client, and - only for a provider <see cref="UsageExtractor.SupportsNativeShape"/> recognizes
    /// - a second, capped copy of the pre-translation native bytes, immune to translation lossiness
    /// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §4). <see cref="NativeBytes"/> is
    /// <see langword="null"/> for a pass-through (no translator ran, so the client bytes already are native)
    /// or an unsupported translated provider (e.g. Gemini). <see cref="TailScanner"/> retained a trailing
    /// window over the client-shape stream, independent of <see cref="ClientShapeBytes"/>'s head cap -
    /// consulted by <see cref="RequestTelemetryPublisher.PublishAsync"/> only when usage extraction fails against both the
    /// head-capped and native captures (§5.11).
    /// </summary>
    private readonly record struct CapturedResponse(byte[] ClientShapeBytes, byte[]? NativeBytes, IncrementalUsageScanner? TailScanner);

    /// <summary>
    /// Owns the "cap the captured bytes, and once the cap is exceeded, lazily allocate an
    /// <see cref="IncrementalUsageScanner"/> to keep scanning a tail window" accounting rule that
    /// <see cref="TranslateAndCaptureStreamAsync"/> and <see cref="CopyAndCaptureAsync"/> each drove
    /// independently (streamed and raw copy-through call shapes) before this type existed - both enforce
    /// the same rule, just triggered from different loops. <see cref="TranslateAndCaptureBufferedAsync"/>
    /// deliberately does not use this type - see its remarks. <see cref="_trackTail"/> disables the tail
    /// scanner for a capture that never consults one (the secondary native-bytes copy in
    /// <see cref="TranslateAndCaptureStreamAsync"/>), so a capacity-exceeding chunk there doesn't pay for a
    /// scanner allocation nothing will ever read.
    /// </summary>
    private sealed class ResponseCaptureAccumulator : IDisposable
    {
        private readonly MemoryStream _capture = new();
        private readonly int _captureCap;
        private readonly bool _trackTail;
        private IncrementalUsageScanner? _tailScanner;

        public ResponseCaptureAccumulator(int captureCap, bool trackTail = true)
        {
            _captureCap = captureCap;
            _trackTail = trackTail;
        }

        /// <summary>The tail scanner allocated once a chunk first exceeded the cap, or <see langword="null"/> if none has (yet), or if this accumulator does not track one.</summary>
        public IncrementalUsageScanner? TailScanner => _tailScanner;

        /// <summary>
        /// Writes up to the remaining cap of <paramref name="chunk"/> into the capture buffer, and - when
        /// tracking a tail scanner - appends <paramref name="chunk"/> to it in full once the cap has been
        /// exceeded, so the very chunk that crosses the cap boundary is still captured in full there, not
        /// just the chunks after it.
        /// </summary>
        public async Task AddAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            if (chunk.IsEmpty)
            {
                return;
            }

            var remainingCapacity = _captureCap - (int)_capture.Length;
            if (remainingCapacity > 0)
            {
                await _capture.WriteAsync(chunk[..Math.Min(chunk.Length, remainingCapacity)], cancellationToken);
            }

            if (_trackTail && remainingCapacity < chunk.Length)
            {
                _tailScanner ??= new IncrementalUsageScanner();
                _tailScanner.Append(chunk.Span);
            }
        }

        /// <summary>Returns the bytes captured so far, up to the configured cap.</summary>
        public byte[] ToArray() => _capture.ToArray();

        /// <inheritdoc />
        public void Dispose() => _capture.Dispose();
    }

    /// <summary>
    /// Reads the entire native (non-streaming) upstream body, runs it through the translator into
    /// OpenAI's shape, writes the translated bytes to the client, and returns both the translated bytes
    /// (capped, for the client-shape parsers) and - for a provider <see cref="UsageExtractor.SupportsNativeShape"/>
    /// recognizes - a second capped copy of the pre-translation native body, for the native telemetry tap
    /// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §4.1). The native body was already fully
    /// materialized in memory to call <see cref="IPayloadTranslator.TranslateResponse"/>, so capturing it
    /// costs nothing extra beyond the capped copy.
    /// </summary>
    private async Task<CapturedResponse> TranslateAndCaptureBufferedAsync(IPayloadTranslator translator, Stream source, Stream destination, int captureCap, CancellationToken cancellationToken)
    {
        try
        {
            using var upstream = new MemoryStream();
            await source.CopyToAsync(upstream, cancellationToken);
            var nativeBytes = upstream.ToArray();

            var translated = translator.TranslateResponse(nativeBytes);
            await destination.WriteAsync(translated, cancellationToken);

            // Deliberately not routed through ResponseCaptureAccumulator: unlike the two loop-based capture
            // methods below, this is a single, already-fully-materialized buffer, and the common case (a
            // response that fits within captureCap) can return the translated array directly instead of
            // paying for a MemoryStream copy the accumulator's chunk-oriented API would otherwise force.
            var clientShapeBytes = translated.Length <= captureCap ? translated : translated[..captureCap];
            byte[]? capturedNativeBytes = UsageExtractor.SupportsNativeShape(translator.Provider)
                ? (nativeBytes.Length <= captureCap ? nativeBytes : nativeBytes[..captureCap])
                : null;

            // Only worth the ~64KB tail-window allocation when the head-capped clientShapeBytes above
            // actually lost data - a response that fits within captureCap is already fully captured, so
            // TryExtractUsage's primary parse never needs the tail fallback.
            IncrementalUsageScanner? tailScanner = null;
            if (translated.Length > captureCap)
            {
                tailScanner = new IncrementalUsageScanner();
                tailScanner.Append(translated);
            }

            return new CapturedResponse(clientShapeBytes, capturedNativeBytes, tailScanner);
        }
        catch (Exception ex) when (ProxyMiddleware.IsStreamAbort(ex) && cancellationToken.IsCancellationRequested)
        {
            // Unlike TranslateAndCaptureStreamAsync, nothing has necessarily reached the client yet here -
            // the whole upstream body is read into memory before the one translated write - so this only
            // fails open (swallows and reports an empty capture) for a genuine client abort, where there is
            // truly no one left to answer. An upstream I/O failure that is NOT the client leaving (checked
            // via cancellationToken.IsCancellationRequested, mirroring IsTransportOutage's identical
            // distinction for the SendAsync case) is deliberately left to propagate, so the caller can still
            // turn it into a real 502 instead of silently committing a 200 with an empty body.
            _logger.LogWarning(ex, "Buffered response to the client was interrupted by a client disconnect; the forward was terminated early.");
            return new CapturedResponse([], null, null);
        }
    }

    /// <summary>
    /// Streams the native SSE upstream through a per-request <see cref="IStreamTranslator"/>, writing
    /// each translated OpenAI-shaped chunk to the client as it is produced and capturing up to
    /// <paramref name="captureCap"/> bytes of the translated stream for telemetry - plus, for a provider
    /// <see cref="UsageExtractor.SupportsNativeShape"/> recognizes, a second capped accumulation of the raw
    /// upstream SSE bytes (before they enter the stream translator), for the native telemetry tap
    /// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §4.1) - the one genuinely new buffer this
    /// plan adds, capped like every other capture here. An embedded provider error throws
    /// <see cref="GeminiStreamException"/> mid-stream (mirroring LiteLLM): the forward is then terminated -
    /// the client sees a truncated stream with no <c>[DONE]</c>, since a 200 OK and earlier chunks have
    /// already been committed to the wire and the status can no longer change.
    /// </summary>
    private async Task<CapturedResponse> TranslateAndCaptureStreamAsync(IPayloadTranslator translator, Stream source, Stream destination, int captureCap, CancellationToken cancellationToken)
    {
        var streamTranslator = translator.CreateStreamTranslator();
        var captureNativeBytes = UsageExtractor.SupportsNativeShape(translator.Provider);
        using var capture = new ResponseCaptureAccumulator(captureCap);
        using var nativeCapture = captureNativeBytes ? new ResponseCaptureAccumulator(captureCap, trackTail: false) : null;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        async Task EmitAsync(byte[] translated)
        {
            if (translated.Length == 0)
            {
                return;
            }

            await destination.WriteAsync(translated, cancellationToken);
            await destination.FlushAsync(cancellationToken);

            await capture.AddAsync(translated, cancellationToken);
        }

        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                if (nativeCapture is not null)
                {
                    await nativeCapture.AddAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }

                await EmitAsync(streamTranslator.Push(buffer.AsSpan(0, bytesRead)));
            }

            await EmitAsync(streamTranslator.Flush());
        }
        catch (GeminiStreamException ex)
        {
            _logger.LogWarning(ex, "Gemini streaming response terminated by an embedded provider error; the client stream was truncated.");
        }
        catch (AnthropicStreamException ex)
        {
            _logger.LogWarning(ex, "Anthropic streaming response terminated by an error event; the client stream was truncated.");
        }
        catch (Exception ex) when (ProxyMiddleware.IsStreamAbort(ex))
        {
            _logger.LogWarning(ex, "Streaming response to the client was interrupted (client disconnected, or the connection was aborted); the forward was terminated early.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new CapturedResponse(capture.ToArray(), nativeCapture?.ToArray(), capture.TailScanner);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> unchanged (the client-facing
    /// forward), while also capturing up to <paramref name="captureCap"/> bytes for telemetry usage
    /// parsing, plus a trailing <see cref="IncrementalUsageScanner"/> window independent of that cap
    /// (§5.11). The capture never delays or alters what reaches <paramref name="destination"/> - it's an
    /// in-memory side copy of each chunk immediately after (not instead of) writing it downstream.
    /// </summary>
    private async Task<(byte[] Captured, IncrementalUsageScanner? TailScanner)> CopyAndCaptureAsync(Stream source, Stream destination, int captureCap, CancellationToken cancellationToken)
    {
        using var capture = new ResponseCaptureAccumulator(captureCap);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                // Unlike a translated response (whose EmitAsync helper flushes every emitted chunk), this
                // is the raw pass-through path for a provider with no translator (Ollama, raw OpenAI, an
                // OpenAI-compatible local server). Without an explicit flush, Kestrel can hold small,
                // infrequent writes - exactly what a slow local model's token-by-token SSE stream produces
                // - in its internal buffer instead of pushing them to the client promptly, so the client
                // sees no bytes at all until the connection eventually closes (or times out first).
                await destination.FlushAsync(cancellationToken);

                await capture.AddAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        catch (Exception ex) when (ProxyMiddleware.IsStreamAbort(ex))
        {
            // Mirrors TranslateAndCaptureStreamAsync's identical fail-open handling: the response has
            // already started, so there is nothing left to do but stop copying and return whatever was
            // captured so far.
            _logger.LogWarning(ex, "Streaming response to the client was interrupted (client disconnected, or the connection was aborted); the forward was terminated early.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (capture.ToArray(), capture.TailScanner);
    }
}
