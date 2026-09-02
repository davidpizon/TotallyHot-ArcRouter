using System.Diagnostics;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Proxy.Bedrock;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Router.Classification;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Invokes the Amazon Bedrock Runtime SDK directly for a route whose provider translates through
/// <see cref="IBedrockPayloadTranslator"/>, mirroring the HTTP forwarding path <see cref="ProxyMiddleware"/>
/// otherwise uses. Extracted from <see cref="ProxyMiddleware"/> (docs/router/code-smell-refactoring-plan.md
/// Phase 2 step 3): a second, parallel invocation path alongside HTTP forwarding, self-contained enough to
/// cut cleanly once <see cref="LocalEndpointResponder"/> and <see cref="RequestTelemetryPublisher"/> had
/// already come out.
/// </summary>
internal sealed class BedrockInvocationHandler
{
    private readonly ILogger _logger;
    private readonly IBedrockRuntimeClientFactory _bedrockClientFactory;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly RequestTelemetryPublisher _requestTelemetryPublisher;

    /// <summary>
    /// Initializes a new instance of the <see cref="BedrockInvocationHandler"/> class.
    /// </summary>
    /// <param name="logger">Logger shared with the owning <see cref="ProxyMiddleware"/> instance.</param>
    /// <param name="bedrockClientFactory">Factory for the Amazon Bedrock Runtime SDK client, owned and disposed by <see cref="ProxyMiddleware"/>.</param>
    /// <param name="circuitBreaker">Per-upstream-target circuit breaker, shared with the HTTP forwarding path so success/failure recorded here is visible there too.</param>
    /// <param name="requestTelemetryPublisher">Telemetry publisher shared with the HTTP forwarding path.</param>
    public BedrockInvocationHandler(
        ILogger logger,
        IBedrockRuntimeClientFactory bedrockClientFactory,
        ICircuitBreaker circuitBreaker,
        RequestTelemetryPublisher requestTelemetryPublisher)
    {
        _logger = logger;
        _bedrockClientFactory = bedrockClientFactory;
        _circuitBreaker = circuitBreaker;
        _requestTelemetryPublisher = requestTelemetryPublisher;
    }

    /// <summary>
    /// Invokes Bedrock for <paramref name="route"/>, forwards the (translated) response to the client, and
    /// publishes routing telemetry for it. Returns <see langword="true"/> once this candidate has been
    /// fully handled - either served successfully or failed in a way with no further backup to try, in
    /// which case an error response has already been written - and <see langword="false"/> when the caller
    /// should fail over to <paramref name="hasNextCandidate"/>'s next candidate instead.
    /// </summary>
    public async Task<bool> InvokeAsync(
        HttpContext context,
        ResolvedModelRoute route,
        IBedrockPayloadTranslator translator,
        byte[] rewrittenBody,
        string requestedModelName,
        bool isFallback,
        bool hasNextCandidate,
        bool nextProviderDiffers,
        float[]? taskEmbedding,
        int routerTokens,
        RoutingSubstitutionReason resolutionReason,
        bool isExploratory = false,
        double propensity = 1.0,
        RequestClassification? classification = null,
        string? taskText = null,
        string? dimBestModel = null)
    {
        var circuitTarget = CircuitBreakerTargetKey.FromRoute(route);
        var nativeRequestBody = translator.TranslateRequest(rewrittenBody);
        var isStreamingRequest = ProxyMiddleware.IsStreamingRequest(rewrittenBody);

        // Not disposed here: the singleton factory owns the client's lifetime and reuses it across
        // requests (AWS SDK clients are thread-safe and meant to be long-lived). See BedrockRuntimeClientFactory.
        var client = _bedrockClientFactory.Create(route);

        var stopwatch = Stopwatch.StartNew();
        byte[] capturedResponseBytes;
        long latencyToHeadersMs;
        IncrementalUsageScanner? tailScanner = null;

        try
        {
            if (isStreamingRequest)
            {
                var request = new InvokeModelWithResponseStreamRequest
                {
                    ModelId = route.ProviderModelId,
                    Body = new MemoryStream(nativeRequestBody),
                    ContentType = "application/json",
                };

                var response = await client.InvokeModelWithResponseStreamAsync(request, context.RequestAborted);
                latencyToHeadersMs = stopwatch.ElapsedMilliseconds;

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers[ProxyMiddleware.RequestedModelHeaderName] = requestedModelName;
                context.Response.Headers[ProxyMiddleware.RoutedModelHeaderName] = route.ModelName;
                context.Response.Headers[ProxyMiddleware.SubstitutionReasonHeaderName] = RequestTelemetryPublisher.ResolveSubstitutionReason(isFallback, resolutionReason).ToString();

                (capturedResponseBytes, tailScanner) = await TranslateAndCaptureBedrockStreamAsync(translator, response.Body, context.Response.Body, ProxyMiddleware.MaxCapturedResponseBytes, context.RequestAborted);
            }
            else
            {
                var request = new InvokeModelRequest
                {
                    ModelId = route.ProviderModelId,
                    Body = new MemoryStream(nativeRequestBody),
                    ContentType = "application/json",
                };

                var response = await client.InvokeModelAsync(request, context.RequestAborted);
                latencyToHeadersMs = stopwatch.ElapsedMilliseconds;

                var translated = translator.TranslateResponse(response.Body.ToArray());

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                context.Response.Headers[ProxyMiddleware.RequestedModelHeaderName] = requestedModelName;
                context.Response.Headers[ProxyMiddleware.RoutedModelHeaderName] = route.ModelName;
                context.Response.Headers[ProxyMiddleware.SubstitutionReasonHeaderName] = RequestTelemetryPublisher.ResolveSubstitutionReason(isFallback, resolutionReason).ToString();
                await context.Response.Body.WriteAsync(translated, context.RequestAborted);

                capturedResponseBytes = translated.Length <= ProxyMiddleware.MaxCapturedResponseBytes ? translated : translated[..ProxyMiddleware.MaxCapturedResponseBytes];

                // Only worth the ~64KB tail-window allocation when capturedResponseBytes above actually
                // lost data - a response that fits within the cap is already fully captured.
                if (translated.Length > ProxyMiddleware.MaxCapturedResponseBytes)
                {
                    var bufferedTailScanner = new IncrementalUsageScanner();
                    bufferedTailScanner.Append(translated);
                    tailScanner = bufferedTailScanner;
                }
            }
        }
        catch (AmazonClientException ex)
        {
            // A client-side SDK failure that never reached AWS at all - most commonly a missing/invalid
            // credential (e.g. "Failed to resolve bearer token in DefaultAWSTokenIdentityResolver" when
            // none of AwsAccessKeyIdEnvVar/AwsSecretAccessKeyEnvVar/AwsSessionTokenEnvVar resolve and the
            // SDK's default credential chain also comes up empty). AmazonServiceException (and its
            // Bedrock-specific subtype below, caught separately) is a *sibling* type - both derive directly
            // from Exception, not from AmazonClientException - so this clause and the
            // AmazonBedrockRuntimeException one below never overlap: a real service-level error (auth
            // rejected *by* AWS, throttling, etc.) only ever matches the other handler. Treated like the HTTP
            // path's 401 handling: logged at Error (not Warning - this is a provider misconfiguration an
            // operator needs to see, not a transient blip), tripping the *provider-wide* circuit
            // (RecordProviderFailure, not the per-target RecordFailure) since a missing/invalid credential
            // breaks every model on this Bedrock provider identically, and surfaced to the client as a 401
            // rather than the generic 502 other Bedrock failures get.
            _circuitBreaker.RecordProviderFailure(route.Provider);
            _logger.LogError(
                ex,
                "Bedrock invocation for provider {Provider} failed to resolve AWS credentials ({Message}); treating as an unauthorized (401) provider-wide outage and bypassing every model on this provider until it recovers.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(ex.Message));

            // Same same-fate reasoning as the HTTP path's 401 handling (see the circuit-breaker comment
            // there): a same-provider backup shares the identical broken credential, so only a genuinely
            // different-provider candidate is worth retrying.
            if (hasNextCandidate && nextProviderDiffers)
            {
                _logger.LogWarning(
                    "Bedrock provider {Provider} failed to authorize for model {Model}; failing over to the next backup.",
                    LogRedaction.Sanitize(route.Provider),
                    LogRedaction.Sanitize(route.ModelName));
                return false;
            }

            if (!context.Response.HasStarted)
            {
                // Generic client message, not ex.Message: an AWS SDK exception can carry internal endpoint,
                // region, or request-id detail. The full exception is logged above for operators.
                await ProxyMiddleware.WriteUpstreamErrorResponseAsync(context, "The upstream provider rejected the request as unauthorized.", StatusCodes.Status401Unauthorized);
            }

            return true;
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            // A Bedrock SDK-level failure (auth, throttling, unknown model id, region misconfiguration,
            // etc.) surfaced before (or, for InvokeModel, without) any bytes reaching the client. Treated
            // like the HTTP path's generic 5xx/outage handling: a per-target circuit failure (RecordFailure,
            // not RecordProviderFailure - unlike the credential case above, this bucket doesn't carry a
            // confident "every model on this provider is equally broken" signal), retried against any next
            // candidate unconditionally (no same-provider exclusion - unlike a shared bad credential, a
            // throttle/misconfiguration on this one model id says nothing about a sibling model's health).
            _circuitBreaker.RecordFailure(circuitTarget);
            _logger.LogWarning(ex, "Bedrock invocation failed for provider {Provider}.", LogRedaction.Sanitize(route.Provider));

            if (hasNextCandidate)
            {
                _logger.LogWarning(
                    "Bedrock provider {Provider} failed for model {Model}; failing over to the next backup.",
                    LogRedaction.Sanitize(route.Provider),
                    LogRedaction.Sanitize(route.ModelName));
                return false;
            }

            if (!context.Response.HasStarted)
            {
                // Generic client message, not ex.Message: an AWS SDK exception can carry internal endpoint,
                // region, or request-id detail. The full exception is logged above for operators.
                await ProxyMiddleware.WriteUpstreamErrorResponseAsync(context, "The upstream provider is unavailable.");
            }

            return true;
        }

        _circuitBreaker.RecordSuccess(circuitTarget);
        var totalDurationMs = stopwatch.ElapsedMilliseconds;

        try
        {
            // Bedrock's native tap is out of scope (docs/router/openai-format-usage-accuracy-plan.md §4.2):
            // its streaming chunks aren't SSE-framed, so the same capture approach doesn't apply. Always
            // null here - telemetry falls back to parsing the translated "openai"-shaped bytes, unchanged
            // from before this plan.
            await _requestTelemetryPublisher.PublishAsync(context, route, requestedModelName, isFallback, "openai", rewrittenBody, capturedResponseBytes, nativeResponseBytes: null, isStreamingRequest, latencyToHeadersMs, totalDurationMs, StatusCodes.Status200OK, context.RequestAborted, upstreamHeaders: null, tailScanner: tailScanner, taskEmbedding: taskEmbedding, routerTokens: routerTokens, resolutionReason: resolutionReason, isExploratory: isExploratory, propensity: propensity, classification: classification, taskText: taskText, dimBestModel: dimBestModel);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish routing telemetry; the forwarded response was unaffected.");
        }

        return true;
    }

    /// <summary>
    /// Translates a Bedrock response stream chunk-by-chunk to the client while capturing (up to
    /// <paramref name="captureCap"/>) what was sent, mirroring <c>TranslateAndCaptureStreamAsync</c>'s role
    /// for the HTTP forwarding path but driven by Bedrock's own <see cref="ResponseStream"/> event
    /// enumeration instead of an SSE byte stream.
    /// </summary>
    private async Task<(byte[] Captured, IncrementalUsageScanner? TailScanner)> TranslateAndCaptureBedrockStreamAsync(IBedrockPayloadTranslator translator, ResponseStream body, Stream destination, int captureCap, CancellationToken cancellationToken)
    {
        var chunkTranslator = translator.CreateBedrockStreamChunkTranslator();
        using var capture = new MemoryStream();
        IncrementalUsageScanner? tailScanner = null;

        async Task EmitAsync(byte[] translated)
        {
            if (translated.Length == 0)
            {
                return;
            }

            await destination.WriteAsync(translated, cancellationToken);
            await destination.FlushAsync(cancellationToken);

            var remainingCapacity = captureCap - (int)capture.Length;
            if (remainingCapacity > 0)
            {
                await capture.WriteAsync(translated.AsMemory(0, Math.Min(translated.Length, remainingCapacity)), cancellationToken);
            }

            if (remainingCapacity < translated.Length)
            {
                // Same lazy-allocation reasoning as CopyAndCaptureAsync: only worth the tail window once
                // the head-capped capture has actually lost bytes, and the boundary-crossing chunk itself
                // still needs to land in the scanner in full.
                tailScanner ??= new IncrementalUsageScanner();
                tailScanner.Append(translated);
            }
        }

        try
        {
            await foreach (var streamEvent in body.WithCancellation(cancellationToken))
            {
                if (streamEvent is PayloadPart part)
                {
                    await EmitAsync(chunkTranslator.TranslateChunk(part.Bytes.ToArray()));
                }

                // A non-PayloadPart event (e.g. a future AWS-added event kind this codebase doesn't yet
                // know about) carries nothing client-visible today - skipped rather than guessed at.
            }

            await EmitAsync(chunkTranslator.Flush());
        }
        catch (AnthropicStreamException ex)
        {
            // An embedded error inside a Bedrock Claude chunk (AnthropicOnBedrockStreamChunkTranslator
            // reuses AnthropicStreamTranslator's per-event handling, which throws on one) - truncate the
            // client stream, mirroring native Anthropic's and Gemini's mid-stream-error handling.
            _logger.LogWarning(ex, "Bedrock Claude streaming response terminated by an error event; the client stream was truncated.");
        }
        catch (ModelStreamErrorException ex)
        {
            // An AWS-level mid-stream error (surfaced by the SDK itself while enumerating ResponseStream,
            // not an embedded provider-JSON error) - same truncation response as the case above.
            _logger.LogWarning(ex, "Bedrock streaming response terminated by a ModelStreamErrorException; the client stream was truncated.");
        }
        catch (Exception ex) when (ProxyMiddleware.IsStreamAbort(ex))
        {
            _logger.LogWarning(ex, "Streaming response to the client was interrupted (client disconnected, or the connection was aborted); the forward was terminated early.");
        }

        return (capture.ToArray(), tailScanner);
    }
}
