using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Quality.Ingress;
using TotallyHot.ArcRouter.Router.Classification;
using TotallyHot.ArcRouter.Router.Embeddings;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Resolves session/turn identity for a served request, extracts its usage and cost, and publishes the
/// resulting <see cref="RoutingTelemetryEvent"/> alongside every other telemetry side-effect (spend
/// tracking, the budget store, the durable usage ledger, transcript capture, and the quality-verifier
/// ingress). Extracted from <see cref="ProxyMiddleware"/> (docs/router/code-smell-refactoring-plan.md
/// Phase 2 step 2): a large, mostly-linear method with a clear single output - telemetry side-effects -
/// that made it a clean second cut after <see cref="LocalEndpointResponder"/>.
/// </summary>
internal sealed class RequestTelemetryPublisher
{
    private readonly IBudgetEnforcer? _budgetStore;
    private readonly IConversationContinuityMatcher _continuityMatcher;
    private readonly IOptionsMonitor<JudgeOptions>? _judgeOptionsMonitor;
    private readonly ILogger _logger;
    private readonly PendingRequestCostCache? _pendingRequestCostCache;
    private readonly PendingRequestProvenanceCache? _pendingRequestProvenanceCache;
    private readonly PendingResponseTextCache? _pendingResponseTextCache;
    private readonly PendingTaskEmbeddingCache? _pendingTaskEmbeddingCache;
    private readonly IModelPriceLookup? _priceLookup;
    private readonly IQualityIngress? _qualityIngress;
    private readonly IResponseTextExtractor _responseTextExtractor;
    private readonly IOptionsMonitor<RoutingOptions>? _routingOptionsMonitor;
    private readonly decimal _selfHostedRouterPricePerMillionTokens;
    private readonly ISessionIdResolver _sessionIdResolver;
    private readonly ISpendTracker _spendTracker;
    private readonly ITelemetryPublisher _telemetryPublisher;
    private readonly ITranscriptStore? _transcriptStore;
    private readonly IConversationTurnTracker _turnTracker;
    private readonly IUsageExtractor _usageExtractor;
    private readonly IUsageLedger? _usageLedger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestTelemetryPublisher"/> class, taking exactly the
    /// collaborators <see cref="PublishAsync"/> needs - the same optional-with-safe-default set
    /// <see cref="ProxyMiddleware"/>'s own constructor already accepts for these, just handed off here
    /// instead of stored as its own fields.
    /// </summary>
    public RequestTelemetryPublisher(
        ILogger logger,
        ISessionIdResolver sessionIdResolver,
        IConversationContinuityMatcher continuityMatcher,
        IConversationTurnTracker turnTracker,
        IUsageExtractor usageExtractor,
        IResponseTextExtractor responseTextExtractor,
        ITelemetryPublisher telemetryPublisher,
        IQualityIngress? qualityIngress,
        ISpendTracker spendTracker,
        IModelPriceLookup? priceLookup,
        IBudgetEnforcer? budgetStore,
        IUsageLedger? usageLedger,
        PendingTaskEmbeddingCache? pendingTaskEmbeddingCache,
        PendingRequestCostCache? pendingRequestCostCache,
        PendingRequestProvenanceCache? pendingRequestProvenanceCache,
        PendingResponseTextCache? pendingResponseTextCache,
        ITranscriptStore? transcriptStore,
        IOptionsMonitor<RoutingOptions>? routingOptionsMonitor,
        IOptionsMonitor<JudgeOptions>? judgeOptionsMonitor,
        decimal selfHostedRouterPricePerMillionTokens)
    {
        _logger = logger;
        _sessionIdResolver = sessionIdResolver;
        _continuityMatcher = continuityMatcher;
        _turnTracker = turnTracker;
        _usageExtractor = usageExtractor;
        _responseTextExtractor = responseTextExtractor;
        _telemetryPublisher = telemetryPublisher;
        _qualityIngress = qualityIngress;
        _spendTracker = spendTracker;
        _priceLookup = priceLookup;
        _budgetStore = budgetStore;
        _usageLedger = usageLedger;
        _pendingTaskEmbeddingCache = pendingTaskEmbeddingCache;
        _pendingRequestCostCache = pendingRequestCostCache;
        _pendingRequestProvenanceCache = pendingRequestProvenanceCache;
        _pendingResponseTextCache = pendingResponseTextCache;
        _transcriptStore = transcriptStore;
        _routingOptionsMonitor = routingOptionsMonitor;
        _judgeOptionsMonitor = judgeOptionsMonitor;
        _selfHostedRouterPricePerMillionTokens = selfHostedRouterPricePerMillionTokens;
    }

    /// <summary>
    /// Resolves the <see cref="RoutingSubstitutionReason"/> actually reported for a served request:
    /// <see cref="RoutingSubstitutionReason.Failover"/> when <paramref name="isFallback"/> is
    /// <see langword="true"/> (the candidate <c>RequestInterceptor</c> lined up first was attempted and
    /// failed at the transport layer, so whatever reason it computed no longer describes what happened),
    /// otherwise <paramref name="resolutionReason"/> unchanged. Public and static because
    /// <see cref="ProxyMiddleware"/> also needs it to set the substitution-reason response header before
    /// this publisher is ever invoked, not just from within <see cref="PublishAsync"/>.
    /// </summary>
    public static RoutingSubstitutionReason ResolveSubstitutionReason(bool isFallback,
        RoutingSubstitutionReason resolutionReason)
    {
        return isFallback ? RoutingSubstitutionReason.Failover : resolutionReason;
    }

    /// <summary>
    /// Resolves session/turn identity, extracts usage and cost, and publishes the resulting
    /// <see cref="RoutingTelemetryEvent"/> together with every other telemetry side-effect for one served
    /// request. Best-effort throughout, matching the original inlined method: a failure in any one
    /// side-effect (transcript capture excepted, which self-guards) is not expected to surface as a
    /// request failure, since the response has already been fully sent to the client by this point.
    /// </summary>
    public async Task PublishAsync(
        HttpContext context,
        ResolvedModelRoute route,
        string requestedModelName,
        bool isFallback,
        string telemetryShapeProvider,
        byte[] rewrittenRequestBody,
        byte[] capturedResponseBytes,
        byte[]? nativeResponseBytes,
        bool isStreaming,
        long latencyToHeadersMs,
        long totalDurationMs,
        int statusCode,
        CancellationToken cancellationToken,
        HttpResponseHeaders? upstreamHeaders = null,
        IncrementalUsageScanner? tailScanner = null,
        float[]? taskEmbedding = null,
        int routerTokens = 0,
        RoutingSubstitutionReason resolutionReason = RoutingSubstitutionReason.None,
        bool isExploratory = false,
        double propensity = 1.0,
        RequestClassification? classification = null,
        string? taskText = null,
        string? dimBestModel = null)
    {
        var (requestBody, sessionId, turnNumber, isSynthesized) =
            ResolveSessionAndTurn(context: context, rewrittenRequestBody: rewrittenRequestBody);

        var (
            requestedModel,
            substitutionReason,
            promptTokens,
            completionTokens,
            cacheCreationTokens,
            cacheReadTokens,
            estimatedCostUsd,
            costConfidence,
            usageExtracted,
            usageShapeProvider,
            usageShapeBytes) = ExtractUsageAndCost(
            route: route,
            requestedModelName: requestedModelName,
            isFallback: isFallback,
            resolutionReason: resolutionReason,
            telemetryShapeProvider: telemetryShapeProvider,
            capturedResponseBytes: capturedResponseBytes,
            nativeResponseBytes: nativeResponseBytes,
            isStreaming: isStreaming,
            tailScanner: tailScanner);

        await RecordSpendAndBudgetAsync(
            route: route,
            requestedModel: requestedModel,
            promptTokens: promptTokens,
            completionTokens: completionTokens,
            cacheCreationTokens: cacheCreationTokens,
            cacheReadTokens: cacheReadTokens,
            estimatedCostUsd: estimatedCostUsd,
            costConfidence: costConfidence,
            usageExtracted: usageExtracted,
            sessionId: sessionId,
            turnNumber: turnNumber,
            upstreamHeaders: upstreamHeaders).ConfigureAwait(false);

        var (newestUserMessage, requestSummary, responseSummary, responseText, correlationId) =
            ExtractResponseTextAndCachePending(
                requestBody: requestBody,
                usageShapeProvider: usageShapeProvider,
                usageShapeBytes: usageShapeBytes,
                isStreaming: isStreaming,
                sessionId: sessionId,
                turnNumber: turnNumber,
                taskEmbedding: taskEmbedding,
                estimatedCostUsd: estimatedCostUsd,
                isExploratory: isExploratory,
                propensity: propensity,
                classification: classification);

        await PersistTranscriptAsync(
            correlationId: correlationId,
            requestedModelName: requestedModelName,
            route: route,
            classification: classification,
            taskText: taskText,
            responseText: responseText,
            estimatedCostUsd: estimatedCostUsd,
            isExploratory: isExploratory,
            propensity: propensity,
            promptTokens: promptTokens,
            completionTokens: completionTokens,
            dimBestModel: dimBestModel).ConfigureAwait(false);

        await PublishTelemetryEventAsync(
            sessionId: sessionId,
            turnNumber: turnNumber,
            isSynthesized: isSynthesized,
            requestedModel: requestedModel,
            route: route,
            isFallback: isFallback,
            promptTokens: promptTokens,
            completionTokens: completionTokens,
            estimatedCostUsd: estimatedCostUsd,
            isStreaming: isStreaming,
            latencyToHeadersMs: latencyToHeadersMs,
            totalDurationMs: totalDurationMs,
            statusCode: statusCode,
            cacheCreationTokens: cacheCreationTokens,
            cacheReadTokens: cacheReadTokens,
            costConfidence: costConfidence,
            requestSummary: requestSummary,
            responseSummary: responseSummary,
            correlationId: correlationId,
            routerTokens: routerTokens,
            substitutionReason: substitutionReason,
            cancellationToken: cancellationToken,
            responseText: responseText,
            newestUserMessage: newestUserMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// M1 sub-phase 1 (docs/router/code-smell-refactoring-plan.md): parses the rewritten request body and
    /// resolves this request's session id and turn number - an explicit id via <see cref="_sessionIdResolver"/>,
    /// or else a synthesized one via <see cref="_continuityMatcher"/>'s message-history matching - and
    /// advances the per-session counter via <see cref="_turnTracker"/>. Logs the same session-resolution
    /// diagnostics <see cref="PublishAsync"/> always has, at the same call site, just moved here verbatim.
    /// </summary>
    private (JsonObject? RequestBody, string SessionId, int TurnNumber, bool IsSynthesized) ResolveSessionAndTurn(
        HttpContext context, byte[] rewrittenRequestBody)
    {
        var requestBody = TryParseJsonObject(rewrittenRequestBody);
        var resolvedSessionId = _sessionIdResolver.Resolve(headers: context.Request.Headers, body: requestBody);

        var isSynthesized = resolvedSessionId is null;
        // No explicit session id found: fall back to matching this request's "messages" array against
        // previously-tracked conversations (see MessageHistoryContinuityMatcher), which itself falls
        // back to a fresh id if nothing matches - covers clients (e.g. GitHub Copilot's OpenAI-compatible
        // model providers) that send no session identifier of any kind, not even under an unrecognized name.
        var sessionId = resolvedSessionId ?? _continuityMatcher.MatchOrTrack(requestBody?["messages"] as JsonArray);
        var turnNumber = _turnTracker.NextTurn(sessionId);

        if (isSynthesized)
        {
            if (turnNumber == 1)
                // No known session-id convention (see SessionIdResolver) matched anything on this
                // request, and no tracked conversation's message history was a prefix of this request's
                // messages either, so this is a brand-new tracked session. Logs header *names* (never
                // values, to avoid leaking auth tokens/cookies) and top-level body *keys* (never values)
                // so an unrecognized client's actual conventions can be spotted and, if there's a stable
                // per-conversation field under a different name, added to SessionIdResolver.
                _logger.LogDebug(
                    message: "No session id found on request to {Path}, and no tracked conversation's message " +
                             "history matched; started tracking new session {SessionId}. Request header names: " +
                             "[{HeaderNames}]. Top-level body keys: [{BodyKeys}].",
                    LogRedaction.Sanitize(context.Request.Path.ToString()),
                    LogRedaction.Sanitize(sessionId),
                    string.Join(separator: ", ",
                        values: context.Request.Headers.Keys
                            .OrderBy(keySelector: k => k, comparer: StringComparer.OrdinalIgnoreCase)
                            .Select(LogRedaction.Sanitize)),
                    requestBody is null
                        ? "(not a JSON object)"
                        : string.Join(separator: ", ",
                            values: requestBody.Select(kv => LogRedaction.Sanitize(kv.Key))));
            else
                _logger.LogDebug(
                    message: "No session id found on request to {Path}, but its message history matched tracked " +
                             "session {SessionId}; treating as turn {TurnNumber}.",
                    LogRedaction.Sanitize(context.Request.Path.ToString()),
                    LogRedaction.Sanitize(sessionId),
                    turnNumber);
        }
        else
        {
            _logger.LogDebug(message: "Resolved session {SessionId}, turn {TurnNumber}.",
                LogRedaction.Sanitize(sessionId), turnNumber);
        }

        return (requestBody, sessionId, turnNumber, isSynthesized);
    }

    /// <summary>
    /// M1 sub-phase 2: resolves the reported requested model and substitution reason, then extracts token
    /// usage - trying, in order, the native (pre-translation) capture, the translated/client-shape capture,
    /// and finally <paramref name="tailScanner"/> (§5.11) - and prices it via <see cref="_priceLookup"/> (or
    /// <see cref="ModelPrice.Free"/> for a free provider). Unchanged fallback order and reasoning from the
    /// original inlined method; see the inline comments below for why each fallback exists.
    /// </summary>
    private (
        string RequestedModel,
        RoutingSubstitutionReason SubstitutionReason,
        int? PromptTokens,
        int? CompletionTokens,
        int? CacheCreationTokens,
        int? CacheReadTokens,
        decimal? EstimatedCostUsd,
        CostConfidence CostConfidence,
        bool UsageExtracted,
        string UsageShapeProvider,
        byte[] UsageShapeBytes) ExtractUsageAndCost(
            ResolvedModelRoute route,
            string requestedModelName,
            bool isFallback,
            RoutingSubstitutionReason resolutionReason,
            string telemetryShapeProvider,
            byte[] capturedResponseBytes,
            byte[]? nativeResponseBytes,
            bool isStreaming,
            IncrementalUsageScanner? tailScanner)
    {
        // The client's literal requested model (docs/router/orchestrator-live-path-plan.md §M2.2) - always
        // distinct from route.ModelName (the model that served) when any substitution or failover
        // occurred; substitutionReason below names why. isFallback additionally tells the dashboard the
        // resolved primary specifically was bypassed at the transport layer. See the failover loop in
        // InvokeAsync. This is the infrastructure-outage cascade, not the paper's Verifier-driven
        // semantic re-routing.
        var requestedModel = requestedModelName;

        // Failover (a transport-level bypass ProxyMiddleware's own loop discovered) always wins over
        // whatever RequestInterceptor knew at resolution time: if isFallback is true, the primary that
        // resolutionReason describes was never actually served, so Failover is the more accurate account
        // of why route differs from requestedModel.
        var substitutionReason = ResolveSubstitutionReason(isFallback: isFallback, resolutionReason: resolutionReason);

        int? promptTokens = null;
        int? completionTokens = null;
        int? cacheCreationTokens = null;
        int? cacheReadTokens = null;
        decimal? estimatedCostUsd = null;
        var costConfidence = CostConfidence.NoUsage;

        // Telemetry stops depending on translation fidelity: when a native (pre-translation) capture was
        // taken, it is parsed under the provider's own key instead of the translated bytes under
        // telemetryShapeProvider - the native shape carries fields (e.g. Anthropic cache tokens) that
        // TranslateResponse/EmitChunk currently drop on the way to the OpenAI-shaped client response (see
        // docs/router/openai-format-usage-accuracy-plan.md §1).
        // Explicit typed locals plus a plain if/else, rather than a ternary/tuple-deconstruction one-liner,
        // so usageShapeBytes's static type is byte[] (not the byte[]? a ternary over nativeResponseBytes
        // would otherwise infer) - the `is { Length: > 0 }` pattern below already proves it non-null on the
        // branch that assigns it.
        string usageShapeProvider;
        byte[] usageShapeBytes;
        bool usedNativeBytes;
        if (nativeResponseBytes is { Length: > 0 } nonEmptyNativeBytes)
        {
            usedNativeBytes = true;
            usageShapeProvider = route.Provider;
            usageShapeBytes = nonEmptyNativeBytes;
        }
        else
        {
            usedNativeBytes = false;
            usageShapeProvider = telemetryShapeProvider;
            usageShapeBytes = capturedResponseBytes;
        }

        var usageExtracted = _usageExtractor.TryExtractUsage(provider: usageShapeProvider, isStreaming: isStreaming,
            bufferedResponseBody: usageShapeBytes, usage: out var usage);
        if (!usageExtracted && usedNativeBytes)
        {
            // The native capture and the translated/client-shape capture are independently truncated at
            // MaxCapturedResponseBytes (see UpstreamResponseWriter.CopyAndCaptureAsync), so a large response can cut the native
            // bytes off before the usage block - often the last thing to arrive in a streamed response -
            // while the other capture still has it. Falling back recovers usage/cost for budget enforcement
            // and the spend ledger instead of recording nothing purely because the preferred capture was
            // the one that got cut off. usageShapeProvider/usageShapeBytes are reassigned too (not just the
            // local usage result), so the response-text extraction below - which reuses the same pair -
            // benefits from the same fallback instead of independently failing against the truncated bytes.
            usageExtracted = _usageExtractor.TryExtractUsage(provider: telemetryShapeProvider, isStreaming: isStreaming,
                bufferedResponseBody: capturedResponseBytes, usage: out usage);
            if (usageExtracted)
            {
                usageShapeProvider = telemetryShapeProvider;
                usageShapeBytes = capturedResponseBytes;
            }
        }

        // Last resort (§5.11): both captures above are head-capped at MaxCapturedResponseBytes, so a
        // response larger than the cap can cut off entirely before reaching its usage block (typically the
        // final SSE event of a streamed response). tailScanner retained a trailing window over the stream
        // independent of that cap - deliberately NOT reassigning usageShapeProvider/usageShapeBytes here,
        // unlike the native-capture fallback above: the response-text extraction below reuses that same
        // pair, and a tail-only buffer holds only the end of a long streamed answer, which would make
        // ResponseSummary a worse (truncated-from-the-front) result than what the head-capped bytes already
        // gave it - the tail is only trustworthy for recovering the trailing usage numbers, not the text.
        if (!usageExtracted && tailScanner is not null)
            usageExtracted = tailScanner.TryExtractUsage(provider: telemetryShapeProvider, isStreaming: isStreaming,
                extractor: _usageExtractor, usage: out usage);

        if (!usageExtracted)
        {
            // Tagged with route.Provider (the real upstream provider), not telemetryShapeProvider: for a
            // translated provider (gemini, ollama), telemetryShapeProvider is forced to "openai" (the
            // shape the extractor parses, not who actually served the request), and the metric's own doc
            // comment promises per-provider attribution so a regression in one translator's output is
            // distinguishable from another's.
            UsageMetrics.ExtractionFailedTotal.Add(1,
                tag: new KeyValuePair<string, object?>(key: "provider", value: route.Provider));
            _logger.LogDebug(
                message:
                "Could not extract usage for provider {Provider} (telemetry shape {TelemetryShapeProvider}, streaming: {IsStreaming}); no cost/token telemetry will be recorded for this request.",
                LogRedaction.Sanitize(route.Provider),
                LogRedaction.Sanitize(telemetryShapeProvider),
                isStreaming);
        }

        if (usageExtracted)
        {
            promptTokens = usage.PromptTokens;
            completionTokens = usage.CompletionTokens;
            cacheCreationTokens = usage.CacheCreationTokens;
            cacheReadTokens = usage.CacheReadTokens;

            // A free provider (a local Ollama runtime, say) has a *known* price of zero. A paid model's
            // price comes from the auto-refreshed price catalog (docs/router/model-price-catalog.md) via
            // _priceLookup; when the catalog has no fresh price for it (lookup returns null, or none was
            // injected), cost stays null - unknown, never silently zero. Zero and unknown are different
            // answers and must not collapse into one. Both real branches run through EstimateCost so there is
            // exactly one cost formula. The catalog keys prices on the client-facing (ModelName, provider)
            // once D3 alias resolution has mapped each source's own naming onto it at ingest
            // (docs/router/d3-alias-resolution.md), so we look up route.ModelName - a model the catalog has no
            // resolved price for simply yields null here, the safe "unknown" outcome.
            // §5.6: the cost-confidence label travels alongside the cost itself, so a caller never has to
            // re-derive from EstimatedCostUsd alone whether "$0" means free, unknown, or an approximate
            // catalog match - see docs/router/token-tracking-improvements.md §5.6.
            if (route.IsFree)
            {
                estimatedCostUsd = ModelPrice.Free.EstimateCost(usage);
                costConfidence = CostConfidence.Exact;
            }
            else if (_priceLookup?.TryGetPrice(new ModelKey(ModelName: route.ModelName, Provider: route.Provider)) is
                     { } price)
            {
                estimatedCostUsd =
                    price.EstimateCost(usage: usage, usedCacheRateFallback: out var usedCacheRateFallback);
                costConfidence = price.IsApproximateMatch || usedCacheRateFallback
                    ? CostConfidence.CatalogApproximate
                    : CostConfidence.Catalog;
            }
            else
            {
                costConfidence = CostConfidence.Unknown;
            }
        }

        return (
            requestedModel,
            substitutionReason,
            promptTokens,
            completionTokens,
            cacheCreationTokens,
            cacheReadTokens,
            estimatedCostUsd,
            costConfidence,
            usageExtracted,
            usageShapeProvider,
            usageShapeBytes);
    }

    /// <summary>
    /// M1 sub-phase 3: records this request's spend, budget usage, durable ledger row, and OTLP/Prometheus
    /// metrics - every one of these best-effort, matching the original inlined method's reasoning for using
    /// <see cref="CancellationToken.None"/> throughout (this runs after the response has already been fully
    /// sent, so recording must not be cancellable by the request's own lifetime).
    /// </summary>
    private async Task RecordSpendAndBudgetAsync(
        ResolvedModelRoute route,
        string requestedModel,
        int? promptTokens,
        int? completionTokens,
        int? cacheCreationTokens,
        int? cacheReadTokens,
        decimal? estimatedCostUsd,
        CostConfidence costConfidence,
        bool usageExtracted,
        string sessionId,
        int turnNumber,
        HttpResponseHeaders? upstreamHeaders)
    {
        // Best-effort, like every other telemetry side-effect on this path - see SpendTracker's own
        // internal try/catch around its file write. Recorded even when usage/cost couldn't be
        // determined, so the running request count stays accurate; it just contributes zero cost/tokens.
        // Uses CancellationToken.None rather than the request's cancellationToken (context.RequestAborted):
        // this runs after the response has already been fully sent, and RequestAborted fires the moment the
        // client disconnects - which for a streaming response happens right as the client finishes reading
        // it - so recording must not be cancellable by the request's own lifetime or it gets silently dropped.
        // Attributed to route.ModelName - the model that actually served (M2.3, decided: a value fix, not
        // a schema change) - not requestedModel, which on an auto-majority deployment would otherwise file
        // nearly all spend under the literal string "auto".
        await _spendTracker.RecordAsync(model: route.ModelName, promptTokens: promptTokens,
            completionTokens: completionTokens, estimatedCostUsd: estimatedCostUsd,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        // Attribute this request's usage to the provider that actually served it (route.Provider is the
        // post-failover, post-budget-skip winner), so per-provider monthly spend and the Governance budget
        // bars stay accurate. Best-effort and self-guarding, exactly like the spend tracker above - same
        // CancellationToken.None reasoning applies. Gated on usageExtracted (unlike the spend tracker
        // above): a zero-usage row here would advance LastUsageAtUtc and make the admin UI report a
        // misleading "last recorded" time for a provider whose response simply carried no usage block.
        if (_budgetStore is not null && usageExtracted)
            await _budgetStore.RecordUsageAsync(
                providerKey: route.Provider,
                costUsd: estimatedCostUsd,
                promptTokens: promptTokens,
                completionTokens: completionTokens,
                cacheCreationTokens: cacheCreationTokens,
                cacheReadTokens: cacheReadTokens,
                usageAtUtc: DateTimeOffset.UtcNow,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

        // The durable usage ledger (docs/router/token-tracking-implementation-plan.md Phase 2): recorded
        // immediately after the budget store, under the same best-effort/CancellationToken.None reasoning,
        // and gated on usageExtracted for the same "don't advance state on a zero-usage row" reason. The
        // dedup key prefers the upstream's own request id (request-id/x-request-id, read from the response
        // headers already in hand here) over the composite hash, so a replayed publish of the same request
        // collides with its own earlier write instead of double-counting.
        if (_usageLedger is not null && usageExtracted)
        {
            var ledgerEntry = new UsageLedgerEntry(
                SessionId: sessionId,
                TurnNumber: turnNumber,
                Provider: route.Provider,
                // The model that served (M2.3) - matches this column's documented meaning
                // (docs/router/agent-cost-tracking.md: "the model this spend belongs to"), not the one
                // lined up first when a substitution or failover occurred.
                RequestedModel: route.ModelName,
                ResolvedModel: route.ProviderModelId,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                CacheCreationTokens: cacheCreationTokens,
                CacheReadTokens: cacheReadTokens,
                EstimatedCostUsd: estimatedCostUsd,
                CostConfidence: costConfidence,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                RequestId: ExtractUpstreamRequestId(upstreamHeaders));
            await _usageLedger.RecordAsync(entry: ledgerEntry, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }

        // §5.12: published unconditionally (not gated on _usageLedger being configured) so an operator who
        // wants OTLP/Prometheus export but not the SQLite ledger still gets it. Creating a Meter/instrument
        // and calling Add on it is cheap with no listener attached - only an actually-configured OTLP
        // exporter (a hosting-level decision, not this class's) turns these into real exported metrics.
        if (usageExtracted)
            EmitUsageMetrics(provider: route.Provider, model: requestedModel, promptTokens: promptTokens,
                completionTokens: completionTokens, cacheCreationTokens: cacheCreationTokens,
                cacheReadTokens: cacheReadTokens, estimatedCostUsd: estimatedCostUsd);
    }

    /// <summary>
    /// M1 sub-phase 4: extracts the newest user message and the response text (used both for the telemetry
    /// event's truncated summaries and for the quality-verifier ingress prompt below), computes this
    /// request's correlation id, and seeds every pending-cache lookup a later-arriving background job
    /// (embedding memory scoring, the judge shadow-scorer) keys off that same correlation id.
    /// </summary>
    private (string? NewestUserMessage, string? RequestSummary, string? ResponseSummary, string? ResponseText, string
        CorrelationId) ExtractResponseTextAndCachePending(
            JsonObject? requestBody,
            string usageShapeProvider,
            byte[] usageShapeBytes,
            bool isStreaming,
            string sessionId,
            int turnNumber,
            float[]? taskEmbedding,
            decimal? estimatedCostUsd,
            bool isExploratory,
            double propensity,
            RequestClassification? classification)
    {
        // Extracted once and reused below for the quality-ingress prompt - both read the same newest-user-
        // message text off the same already-parsed requestBody, so a second walk of its messages array
        // would just repeat the first.
        var newestUserMessage = RequestTextExtractor.ExtractNewestUserMessage(requestBody);
        var requestSummary = TextTruncator.Truncate(newestUserMessage);
        var responseSummary = _responseTextExtractor.TryExtractText(provider: usageShapeProvider,
            isStreaming: isStreaming, bufferedResponseBody: usageShapeBytes, text: out var responseText)
            ? TextTruncator.Truncate(responseText)
            : null;

        // The raw-body log above ([INTERCEPTOR] Intercepted agent response message) dumps the response
        // exactly as it crossed the wire - for a streamed completion that's dozens of single-token SSE
        // "delta" chunks on one log line, so the answer text itself is never a contiguous, searchable
        // substring (e.g. "The application's name is Totally Hot Arc Router." appears only as separate
        // " The", " application", "'s", " name", ... tokens). Logging the assembled text extracted above
        // for telemetry gives the actual answer as one readable, searchable line.
        _logger.LogDebug(
            message: "[INTERCEPTOR] Assembled LLM response text: {ResponseText}",
            responseSummary is null ? "(none found)" : LogRedaction.Truncate(LogRedaction.Sanitize(responseText)));

        // A stable id shared by this telemetry event and any off-path quality signal derived from the same
        // response, so a dashboard can join the two.
        var correlationId = FormattableString.Invariant($"{sessionId}:{turnNumber}");

        // docs/router/live-feedback-learning-plan.md Phase 2c: this is the earliest point the correlation
        // id a later-arriving QualityResult carries is actually known - RequestInterceptor.ResolveModelRouteAsync
        // computed taskEmbedding well before session/turn resolution ran, so it could not key this itself.
        // Recorded here, immediately once both halves exist, rather than passed to RequestInterceptor.
        if (taskEmbedding is not null)
            _pendingTaskEmbeddingCache?.Set(correlationId: correlationId, embedding: taskEmbedding);

        // docs/router/self-organizing-classification-plan.md Phase T1c: mirrors the embedding cache
        // Set above exactly - same correlation id, same "this is the earliest point the value is known
        // alongside the correlation id" reasoning - so EmbeddingMemoryScoreObserver can recover the real
        // cost and provenance once the verifier score arrives instead of writing cost 0.0 / certain
        // non-exploratory provenance unconditionally.
        _pendingRequestCostCache?.Set(correlationId: correlationId, cost: estimatedCostUsd ?? 0m);
        _pendingRequestProvenanceCache?.Set(correlationId: correlationId, isExploratory: isExploratory,
            propensity: propensity, dimension: classification?.Dimension);

        // docs/router/geval-shadow-scoring-plan.md §Raw-text preservation: the response text is already in
        // hand from the TryExtractText call above (responseSummary's source) - this adds retention only,
        // for JudgeShadowScoreObserver's later-arriving background job to recover by TryTake. Gated on
        // extraction having actually succeeded (responseText is only assigned when TryExtractText returns
        // true) and on the judge being switched on right now - read from the monitor, not captured once,
        // exactly like the EnableAdaptiveRouting gate below. That live read is the whole point here: the
        // judge toggle is what authorizes retaining raw response text in memory at all, so switching it off
        // has to stop retention immediately rather than at the next restart.
        if (responseSummary is not null && (_judgeOptionsMonitor?.CurrentValue.Enabled ?? false))
            _pendingResponseTextCache?.Set(correlationId: correlationId, text: responseText);

        return (newestUserMessage, requestSummary, responseSummary, responseText, correlationId);
    }

    /// <summary>
    /// M1 sub-phase 5: writes this request's single transcript row, best-effort and off the hot path in
    /// spirit (the response has already been fully sent to the client by this point). Gated on
    /// <see cref="Models.RoutingOptions.EnableAdaptiveRouting"/> read live from <see cref="_routingOptionsMonitor"/>
    /// (not captured once), and wrapped in its own try/catch so a transcript-store failure can never surface
    /// as a routing failure, matching every other telemetry side-effect on this path.
    /// </summary>
    private async Task PersistTranscriptAsync(
        string correlationId,
        string requestedModelName,
        ResolvedModelRoute route,
        RequestClassification? classification,
        string? taskText,
        string? responseText,
        decimal? estimatedCostUsd,
        bool isExploratory,
        double propensity,
        int? promptTokens,
        int? completionTokens,
        string? dimBestModel)
    {
        // docs/router/self-organizing-classification-plan.md Phase T1a/T1b: the transcript store's single
        // insert. Best-effort and off the hot path in spirit (the response has already been fully sent to
        // the client by this point) - gated on TranscriptOptions.Enabled so a disabled install creates no
        // table and writes nothing, and wrapped in its own try/catch so a transcript-store failure can
        // never surface as a routing failure, matching every other telemetry side-effect in this method.
        // Phase T6 adds RoutingOptions.EnableAdaptiveRouting as a second, live gate - read from the
        // monitor (not captured once) so toggling it stops or resumes writes without a restart.
        if (_transcriptStore is not null && (_routingOptionsMonitor?.CurrentValue.EnableAdaptiveRouting ?? false))
            try
            {
                await _transcriptStore.InsertAsync(
                    record: new TranscriptRecord(
                        0,
                        CorrelationId: correlationId,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        RequestedModel: requestedModelName,
                        RoutedModel: route.ModelName,
                        Dimension: classification?.Dimension,
                        Difficulty: classification?.Difficulty,
                        Language: classification?.Language,
                        IsUtility: classification?.IsUtility ?? false,
                        PromptText: taskText,
                        ResponseText: responseText,
                        null,
                        Cost: estimatedCostUsd,
                        IsExploratory: isExploratory,
                        Propensity: propensity,
                        InputTokens: promptTokens,
                        OutputTokens: completionTokens,
                        null,
                        DimBestModel: dimBestModel),
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(exception: ex,
                    message: "Failed to write a transcript row for correlation {CorrelationId}; continuing without it.",
                    correlationId);
            }
    }

    /// <summary>
    /// M1 sub-phase 6: builds the <see cref="RoutingTelemetryEvent"/> for this request, publishes it via
    /// <see cref="_telemetryPublisher"/>, and - off-path, best-effort - hands the completed response to the
    /// quality verifier ingress. The last side-effect on this path, matching the original inlined method's
    /// ordering.
    /// </summary>
    private async Task PublishTelemetryEventAsync(
        string sessionId,
        int turnNumber,
        bool isSynthesized,
        string requestedModel,
        ResolvedModelRoute route,
        bool isFallback,
        int? promptTokens,
        int? completionTokens,
        decimal? estimatedCostUsd,
        bool isStreaming,
        long latencyToHeadersMs,
        long totalDurationMs,
        int statusCode,
        int? cacheCreationTokens,
        int? cacheReadTokens,
        CostConfidence costConfidence,
        string? requestSummary,
        string? responseSummary,
        string correlationId,
        int routerTokens,
        RoutingSubstitutionReason substitutionReason,
        CancellationToken cancellationToken,
        string? responseText,
        string? newestUserMessage)
    {
        // What routing this request cost us, charged at the self-hosted rate (research-doc §5.1: TotTok is
        // router + model, so routing overhead is the router's to carry). Kept separate from
        // estimatedCostUsd - which is what the upstream provider charged - because a savings figure has to
        // be net of this, and folding the two together would make that impossible to unwind downstream.
        var routerCostUsd = routerTokens / 1_000_000m * _selfHostedRouterPricePerMillionTokens;

        var telemetryEvent = new RoutingTelemetryEvent(
            SessionId: sessionId,
            TurnNumber: turnNumber,
            IsSessionSynthesized: isSynthesized,
            RequestedModel: requestedModel,
            ResolvedModel: route.ProviderModelId,
            Provider: route.Provider,
            IsFallback: isFallback,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            EstimatedCostUsd: estimatedCostUsd,
            IsStreaming: isStreaming,
            LatencyToHeadersMs: latencyToHeadersMs,
            TotalDurationMs: totalDurationMs,
            StatusCode: statusCode,
            TimestampUtc: DateTimeOffset.UtcNow,
            RoutedModel: route.ModelName,
            CacheCreationTokens: cacheCreationTokens,
            CacheReadTokens: cacheReadTokens,
            CostConfidence: costConfidence,
            RequestSummary: requestSummary,
            ResponseSummary: responseSummary,
            CorrelationId: correlationId,
            RouterTokens: routerTokens,
            RouterCostUsd: routerCostUsd,
            SubstitutionReason: substitutionReason);

        await _telemetryPublisher.PublishAsync(telemetryEvent: telemetryEvent, cancellationToken: cancellationToken);

        // Off-path, best-effort: hand the completed response to the quality verifier for static and
        // scoring. The ingress samples, extracts, and enqueues without blocking; it never throws. Reuses
        // the already-extracted (untruncated) response text so no second copy of the body is made.
        if (_qualityIngress is not null && !string.IsNullOrEmpty(responseText))
            _qualityIngress.TryIngest(new QualityIngestContext(
                ResponseText: responseText,
                Prompt: newestUserMessage ?? string.Empty,
                Model: requestedModel,
                CorrelationId: correlationId,
                SessionId: sessionId));
    }

    /// <summary>
    /// Publishes one request's usage to <see cref="UsageMetrics"/> (§5.12): a token count per non-zero
    /// dimension, cost when known, and <see cref="UsageMetrics.UnpricedRequestsTotal"/> when it isn't -
    /// mirroring the ledger's own "unknown is not zero" distinction rather than defaulting a missing cost
    /// to 0.0 in the exported metric.
    /// </summary>
    private static void EmitUsageMetrics(string provider, string model, int? promptTokens, int? completionTokens,
        int? cacheCreationTokens, int? cacheReadTokens, decimal? estimatedCostUsd)
    {
        void AddTokens(int? value, string kind)
        {
            if (value is > 0)
                UsageMetrics.TokensTotal.Add(
                    delta: value.Value,
                    tag1: new KeyValuePair<string, object?>(key: "provider", value: provider),
                    tag2: new KeyValuePair<string, object?>(key: "model", value: model),
                    tag3: new KeyValuePair<string, object?>(key: "kind", value: kind));
        }

        AddTokens(value: promptTokens, kind: "prompt");
        AddTokens(value: completionTokens, kind: "completion");
        AddTokens(value: cacheCreationTokens, kind: "cache_creation");
        AddTokens(value: cacheReadTokens, kind: "cache_read");

        if (estimatedCostUsd is decimal cost)
            UsageMetrics.CostUsdTotal.Add(
                delta: (double)cost,
                tag1: new KeyValuePair<string, object?>(key: "provider", value: provider),
                tag2: new KeyValuePair<string, object?>(key: "model", value: model));
        else
            UsageMetrics.UnpricedRequestsTotal.Add(1,
                tag: new KeyValuePair<string, object?>(key: "provider", value: provider));
    }

    /// <summary>
    /// Reads the upstream's own request id off <paramref name="headers"/> - <c>request-id</c> (Anthropic)
    /// or, failing that, <c>x-request-id</c> (the more common convention) - for
    /// <see cref="UsageLedgerEntry.RequestId"/>. Returns <see langword="null"/> when
    /// <paramref name="headers"/> is absent (the Bedrock invocation path has no HTTP response headers to
    /// read) or neither header was sent, in which case <see cref="Telemetry.UsageLedger"/> falls back to
    /// its composite dedup key.
    /// </summary>
    private static string? ExtractUpstreamRequestId(HttpResponseHeaders? headers)
    {
        if (headers is null) return null;

        if (headers.TryGetValues(name: "request-id", values: out var requestIdValues))
            return requestIdValues.FirstOrDefault();

        if (headers.TryGetValues(name: "x-request-id", values: out var xRequestIdValues))
            return xRequestIdValues.FirstOrDefault();

        return null;
    }

    /// <summary>
    /// Attempts to parse the given bytes as a JSON object, returning null if they are not valid JSON or not an
    /// object.
    /// </summary>
    private static JsonObject? TryParseJsonObject(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}