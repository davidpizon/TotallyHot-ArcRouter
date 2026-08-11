using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Classification;
using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Extraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Proxy
{
    /// <summary>
    /// Sits between the proxy's inbound HTTP pipeline and the upstream forward: resolves each request's
    /// requested model against the known-model allowlist, rewrites it to the upstream provider's model
    /// id, and answers the client-facing model discovery endpoint.
    /// </summary>
    public class RequestInterceptor
    {
        /// <summary>
        /// The reserved, client-facing model name that explicitly asks the router to choose the model
        /// itself instead of naming one - the same auto-select the generalized fallback performs for an
        /// unresolved name, but requested deliberately rather than as a recovery. Matched
        /// case-insensitively, and it wins over a configured model of the same name so the meaning of
        /// <c>"model": "auto"</c> never depends on the operator's model list.
        /// </summary>
        internal const string AutoSelectModelName = "auto";

        /// <summary>
        /// The neutral prior assigned to a candidate with no recorded <see cref="RouterMemory"/> score yet
        /// (cold start), used when ranking fallback/substitute candidates. Cold-start candidates therefore
        /// interleave with scored ones (score range 0.0-1.0) instead of always sinking to the bottom or
        /// jumping to the top.
        /// </summary>
        private const double ColdStartRankingScore = 0.5;

        private readonly ILogger<RequestInterceptor> _logger;
        private readonly IModelRouteResolver _modelRouteResolver;
        private readonly string? _forcedModelName;
        private readonly RouterMemory? _routerMemory;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly IDimensionInferrer _dimensionInferrer;
        private readonly IRequestClassifier _requestClassifier;
        private readonly string _liveMemoryPrefix;
        private readonly IRoutingPolicy? _routingPolicy;

        /// <summary>Number of requests seen by <see cref="InterceptRequestAsync"/> so far.</summary>
        public int InterceptedRequestCount { get; private set; }

        /// <param name="logger">The logger.</param>
        /// <param name="modelRouteResolver">The known-model allowlist/resolver.</param>
        /// <param name="singleModelServingOptions">
        /// Optional Local Proxy CLI single-model override (see <see cref="SingleModelServingOptions"/>).
        /// When its <see cref="SingleModelServingOptions.ForcedModelName"/> is set, it must already be one of
        /// <paramref name="modelRouteResolver"/>'s configured models - checked eagerly here so an invalid
        /// <c>--model</c> CLI value fails at startup, not on the first request.
        /// </param>
        /// <param name="routerMemory">
        /// Optional score memory consulted whenever a substitute/fallback candidate must be ranked (an
        /// unresolved <c>model</c> - see <see cref="ResolveModelRouteAsync"/> - or a circuit-open primary,
        /// see <c>docs/router/agent-resilience-strategies.md</c>). <see langword="null"/> disables
        /// score-based ranking (every candidate is treated as cold-start).
        /// </param>
        /// <param name="circuitBreaker">
        /// Optional per-upstream-target circuit breaker (<c>docs/router/agent-resilience-strategies.md</c>).
        /// Defaults to a fresh, always-CLOSED <see cref="TotallyHot.ArcRouter.Proxy.CircuitBreaker"/> instance when
        /// omitted - behaviorally inert until something records a failure against it. In the real app this
        /// must be the <em>same</em> DI singleton instance also given to <see cref="ProxyMiddleware"/> (see
        /// <c>ServiceCollectionExtensions</c>), since <see cref="ProxyMiddleware"/> is what records
        /// successes/failures this class reads back when ranking candidates.
        /// </param>
        /// <param name="dimensionInferrer">
        /// Infers the live dimension of the request in flight from its newest user message. Defaults to a
        /// fresh <see cref="KeywordDimensionInferrer"/> when omitted - the same heuristic the sandbox's
        /// post-response path uses, so a request and its own later-observed score are classified
        /// identically. Also the default <paramref name="requestClassifier"/>'s dimension source when
        /// that parameter itself is omitted.
        /// </param>
        /// <param name="sandboxOptions">
        /// Optional source of <see cref="SandboxOptions.LiveMemoryPrefix"/>, which must match what
        /// <see cref="Router.RouterMemoryScoreObserver"/> writes under for <paramref name="routerMemory"/>
        /// lookups to ever hit. Defaults to <see cref="SandboxOptions"/>'s own default prefix when omitted.
        /// </param>
        /// <param name="requestClassifier">
        /// PLAN.md Phase H's Context-leg classifier, run ahead of routing on every request. Defaults to a
        /// <see cref="HeuristicRequestClassifier"/> built over <paramref name="dimensionInferrer"/> when
        /// omitted, so its dimension output matches <see cref="ResolveModelRouteAsync"/>'s classification by construction.
        /// </param>
        /// <param name="routingPolicy">
        /// PLAN.md Phase I's Action leg (<c>docs/router/utility-model-routing.md</c> §B4): consulted, when
        /// supplied, to pick the model for the router alias and the unresolved-model fallback instead of
        /// the memory-only ranking <see cref="RankEligibleModels"/> otherwise falls back to. <see langword="null"/>
        /// (the default) preserves the pre-Phase-I memory-only behavior exactly.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="singleModelServingOptions"/> names a model that isn't configured.
        /// </exception>
        public RequestInterceptor(
            ILogger<RequestInterceptor> logger,
            IModelRouteResolver modelRouteResolver,
            SingleModelServingOptions? singleModelServingOptions = null,
            RouterMemory? routerMemory = null,
            ICircuitBreaker? circuitBreaker = null,
            IDimensionInferrer? dimensionInferrer = null,
            IOptions<SandboxOptions>? sandboxOptions = null,
            IRequestClassifier? requestClassifier = null,
            IRoutingPolicy? routingPolicy = null)
        {
            _logger = logger;
            _modelRouteResolver = modelRouteResolver;
            _forcedModelName = singleModelServingOptions?.ForcedModelName;
            _routerMemory = routerMemory;
            _circuitBreaker = circuitBreaker ?? new CircuitBreaker();
            _dimensionInferrer = dimensionInferrer ?? new KeywordDimensionInferrer();
            _liveMemoryPrefix = sandboxOptions?.Value.LiveMemoryPrefix ?? new SandboxOptions().LiveMemoryPrefix;
            _requestClassifier = requestClassifier ?? new HeuristicRequestClassifier(_dimensionInferrer);
            _routingPolicy = routingPolicy;

            if (_forcedModelName is not null &&
                !modelRouteResolver.ListModels().Any(m => string.Equals(m.ModelName, _forcedModelName, StringComparison.OrdinalIgnoreCase)))
            {
                var configuredNames = string.Join(", ", modelRouteResolver.ListModels().Select(m => m.ModelName));
                throw new InvalidOperationException(
                    $"--model '{_forcedModelName}' is not a configured model. Configured models: {configuredNames}");
            }
        }

        /// <summary>
        /// Intercepts an incoming HTTP request before it is forwarded.
        /// </summary>
        /// <param name="context">The HTTP context.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public Task InterceptRequestAsync(HttpContext context)
        {
            _logger.LogInformation(
                "[INTERCEPTOR] Intercepting request for {Method} {Scheme}://{Host}{Path}",
                SanitizeForLog(context.Request.Method),
                SanitizeForLog(context.Request.Scheme),
                SanitizeForLog(context.Request.Host.ToString()),
                SanitizeForLog(context.Request.Path.ToString()));
            InterceptedRequestCount++;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Intercepts the response from the target server before it is sent to the client.
        /// </summary>
        /// <param name="context">The HTTP context.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public Task InterceptResponseAsync(HttpContext context)
        {
            _logger.LogInformation("[INTERCEPTOR] Intercepting response for {Path} with status {StatusCode}", SanitizeForLog(context.Request.Path.ToString()), context.Response.StatusCode);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Reports whether <paramref name="provider"/> is currently switched on (Governance &gt; Providers'
        /// Stop/Play toggle). Exposed so <see cref="ProxyMiddleware"/> - which holds only this interceptor,
        /// not the resolver itself - can apply the same gate immediately before attempting a candidate.
        /// </summary>
        public bool IsProviderEnabled(string provider) => _modelRouteResolver.IsProviderEnabled(provider);

        /// <summary>
        /// Reports whether <paramref name="modelName"/> is currently routable (Governance &gt; Providers'
        /// per-model Start/Stop toggle, and the last "Refresh from endpoint" scan's presence result).
        /// Exposed so <see cref="ProxyMiddleware"/> can apply the same gate immediately before attempting a
        /// candidate, mirroring <see cref="IsProviderEnabled"/>.
        /// </summary>
        public bool IsModelEnabled(string modelName) => _modelRouteResolver.IsModelEnabled(modelName);

        /// <summary>
        /// Lists the client-facing models this proxy is configured to route. Used to answer the
        /// OpenAI-compatible model discovery endpoint (<c>GET /v1/models</c>). When single-model serving
        /// is forced (see the constructor's <c>singleModelServingOptions</c> parameter), only that one
        /// model is listed, so a connecting tool sees exactly what it can actually get.
        /// </summary>
        public IReadOnlyList<AvailableModel> ListAvailableModels()
        {
            var models = _modelRouteResolver.ListModels();

            return _forcedModelName is null
                ? models
                : models.Where(m => string.Equals(m.ModelName, _forcedModelName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Reads the request body, resolves the requested model against the known-model allowlist, and
        /// rewrites <c>model</c> to the upstream provider's model id. The proxy only ever forwards to
        /// upstreams present in this allowlist, so a request can never be routed back to the proxy itself.
        /// The reserved name <see cref="AutoSelectModelName"/> (any casing) skips the lookup and lets the
        /// router auto-select the highest-ranked currently-eligible model instead.
        /// </summary>
        /// <param name="context">The HTTP context.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="ModelRouteResolutionResult"/> describing the outcome.</returns>
        public async Task<ModelRouteResolutionResult> ResolveModelRouteAsync(HttpContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            string body;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return ModelRouteResolutionResult.Failure("Request body must be a JSON object containing a 'model' field.");
            }

            JsonNode? json;
            try
            {
                json = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                return ModelRouteResolutionResult.Failure("Request body is not valid JSON.");
            }

            if (json is not JsonObject jsonObject)
            {
                return ModelRouteResolutionResult.Failure("Request body must be a JSON object containing a 'model' field.");
            }

            // PLAN.md Phase G: the dimension this request's own routing decision reads is the same one a
            // later-arriving sandbox score for this same prompt will be written under (RouterMemoryScoreObserver),
            // so a verifier score written on request N can actually change the model selected on request N+1.
            // PLAN.md Phase H/I: the classification is computed once here and reused both for the dimension
            // key and for IsUtility, so the dimension key and the tier IRoutingPolicy sees can never
            // be classified differently for the same request.
            var classification = _requestClassifier.Classify(jsonObject);
            var liveDimension = RouterDimension.ToLiveKey(_liveMemoryPrefix, classification.Dimension);

            var modelName = jsonObject["model"] is JsonValue modelValue && modelValue.TryGetValue<string>(out var value)
                ? value
                : null;

            if (string.IsNullOrWhiteSpace(modelName))
            {
                return ModelRouteResolutionResult.Failure("Request body must include a non-empty 'model' field.");
            }

            // Local Proxy CLI: single-model serving ignores whatever model the client
            // asked for and always routes to the one CLI-forced model (already confirmed configured in
            // the constructor), mirroring LiteLLM's "litellm --model provider/name" behavior.
            if (_forcedModelName is not null)
            {
                modelName = _forcedModelName;
            }

            // "model": "auto" (any casing) is an explicit request for the router to pick, so it skips the
            // allowlist lookup entirely and runs the same ranked auto-select the unresolved-model fallback
            // below uses. Checked before TryResolve so the reserved name can't be shadowed by a configured
            // model literally named "auto". Single-model serving is unaffected: _forcedModelName has already
            // overwritten modelName above, so a forced proxy still serves its one model.
            var isAutoSelectRequest =
                _forcedModelName is null && string.Equals(modelName, AutoSelectModelName, StringComparison.OrdinalIgnoreCase);

            ResolvedModelRoute? route;

            if (isAutoSelectRequest)
            {
                var autoSelectedRoute = await ResolveAgenticRouteAsync(classification, liveDimension, cancellationToken);
                if (autoSelectedRoute is null)
                {
                    // Same "everything is unavailable" condition the fallback path reports, but phrased for a
                    // caller who asked for auto-select rather than one who named a model we didn't recognize.
                    _logger.LogWarning("[INTERCEPTOR] Rejected auto-select request: no eligible model is currently available.");
                    return ModelRouteResolutionResult.Failure(
                        $"model '{AutoSelectModelName}' could not be auto-selected: no eligible model is currently available.");
                }

                _logger.LogInformation(
                    "[INTERCEPTOR] Auto-select requested; routed to '{ResolvedModel}'.",
                    SanitizeForLog(autoSelectedRoute.ModelName));
                route = autoSelectedRoute;
            }
            else if (!_modelRouteResolver.TryResolve(modelName, out route) || !_modelRouteResolver.IsModelEnabled(modelName))
            {
                // Docs/router/utility-model-routing.md's generalized fallback: outside forced single-model
                // serving, an unresolved model name is a routing decision, not a hard error - accept the
                // request and let TryAgenticallyRouteUnresolvedModel pick a real, allowlisted candidate.
                // Single-model serving keeps its existing eager-validated behavior untouched (never reaches
                // here with _forcedModelName set, since it overrides modelName above before this check).
                // A model that resolved but is stopped/not-currently-upstream (IsModelEnabled false) is
                // treated identically to an unresolved name - same fallback, same rejection message - rather
                // than silently routing to a model the operator just disabled or the endpoint stopped
                // reporting.
                var agenticRoute = _forcedModelName is null
                    ? await ResolveAgenticRouteAsync(classification, liveDimension, cancellationToken)
                    : null;

                if (agenticRoute is not null)
                {
                    _logger.LogInformation(
                        "[INTERCEPTOR] Unresolved model '{ModelName}' accepted and agentically routed to '{ResolvedModel}'.",
                        SanitizeForLog(modelName), SanitizeForLog(agenticRoute.ModelName));
                    route = agenticRoute;
                }
                else
                {
                    _logger.LogWarning("[INTERCEPTOR] Rejected request for unknown model '{ModelName}'.", SanitizeForLog(modelName));
                    return ModelRouteResolutionResult.Failure($"model '{modelName}' is not in the known model list.");
                }
            }

            List<RouteCandidate> candidates;

            if (_forcedModelName is not null)
            {
                // Local Proxy CLI single-model serving: one operator-chosen model, no substitution and no
                // fallback chain - identical to the prior behavior. This candidate list is not itself
                // filtered by circuit-breaker state (there is nothing to substitute it with), but
                // ProxyMiddleware still applies its real-time ShouldBypass/ShouldBypassProvider checks to
                // this forced candidate like any other, so a request still fails fast with a 502 if the
                // forced model's target or provider is currently OPEN.
                candidates = [BuildCandidate(jsonObject, route)];
            }
            else
            {
                // docs/router/agent-resilience-strategies.md's Circuit Breaker: when the resolved primary's
                // own upstream target is presently OPEN (unhealthy, still cooling down) - or its whole
                // provider is (e.g. a 401 tripped every model on that provider at once, see
                // ICircuitBreaker.RecordProviderFailure) - or the provider has been switched off via
                // Governance > Providers' Stop control - or the model itself has been stopped or dropped by
                // its provider's last scan - swap in the next-best eligible model instead of ever attempting
                // it - "the router bypasses this agent entirely... without making a network call." If
                // literally nothing else is eligible (every configured model, including this one, is open or
                // disabled), there's no substitute to swap in; the original route is kept and
                // ProxyMiddleware's own real-time bypass check (immediately before the actual attempt) will
                // then fail fast with a 502/503 without making the call, which is the correct "everything is
                // unavailable" outcome.
                if (_circuitBreaker.IsOpen(CircuitBreakerTargetKey.FromRoute(route)) ||
                    _circuitBreaker.IsProviderOpen(route.Provider) ||
                    !_modelRouteResolver.IsProviderEnabled(route.Provider) ||
                    !_modelRouteResolver.IsModelEnabled(route.ModelName))
                {
                    var substitute = RankEligibleModels([route.ModelName], liveDimension).FirstOrDefault();
                    if (substitute is not null)
                    {
                        _logger.LogInformation(
                            "[INTERCEPTOR] Model '{ModelName}' circuit is open; substituting next-best model '{Substitute}'.",
                            SanitizeForLog(route.ModelName), SanitizeForLog(substitute.ModelName));
                        route = substitute;
                    }
                }

                // Build the ordered list of upstreams to try: the (possibly substituted) primary, then every
                // other currently-eligible configured model ranked by RouterMemory score - the dynamic
                // replacement for the old static per-model Fallbacks list. Each candidate gets its own
                // rewritten body because a backup on a different provider needs a different upstream model
                // id substituted into the same request.
                candidates = [BuildCandidate(jsonObject, route)];

                var seenTargets = new HashSet<CircuitBreakerTargetKey> { CircuitBreakerTargetKey.FromRoute(route) };
                foreach (var fallbackRoute in RankEligibleModels([route.ModelName], liveDimension))
                {
                    // Skip a candidate that resolves to the same upstream target (same provider, base URL,
                    // and model id) as one already queued - a duplicate hop would just repeat the same
                    // failing call. Keyed on the full target, not ProviderModelId alone, so two genuinely
                    // distinct providers that happen to share a model-id string are both kept as valid hops.
                    if (seenTargets.Add(CircuitBreakerTargetKey.FromRoute(fallbackRoute)))
                    {
                        candidates.Add(BuildCandidate(jsonObject, fallbackRoute));
                    }
                }
            }

            return ModelRouteResolutionResult.Success(candidates);
        }

        /// <summary>
        /// Picks a real, allowlisted route to serve a request whose <c>model</c> didn't match any configured
        /// entry - the generalized fallback in <c>docs/router/utility-model-routing.md</c> - and also to serve
        /// a request that explicitly asked for auto-select via <see cref="AutoSelectModelName"/>.
        /// </summary>
        /// <remarks>
        /// PLAN.md Phase I / <c>docs/router/utility-model-routing.md</c> §B4: when a routing policy is
        /// configured (<see cref="_routingPolicy"/>), this builds a <see cref="RoutingContext"/> from every
        /// currently-eligible candidate and lets it choose - cost-aware and quality-gated for utility
        /// traffic, <see cref="AgentAsARouter"/>'s memory ranking otherwise. A policy pick that doesn't
        /// resolve to a live route (e.g. it named a model outside the configured <c>ModelList</c>) degrades
        /// to the memory-only ranking rather than failing the request. With no policy configured - the
        /// pre-Phase-I default - this is exactly <see cref="RankEligibleModels"/>'s top pick, unchanged.
        /// </remarks>
        /// <param name="classification">The request's Phase H classification, from <see cref="ResolveModelRouteAsync"/>.</param>
        /// <param name="liveDimension">The request's live dimension key, from <see cref="ResolveModelRouteAsync"/>.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The resolved route to serve the request with, or <see langword="null"/> when no eligible model is currently available.</returns>
        private async Task<ResolvedModelRoute?> ResolveAgenticRouteAsync(
            RequestClassification classification,
            string liveDimension,
            CancellationToken cancellationToken)
        {
            if (_routingPolicy is not null)
            {
                var candidates = BuildRoutingCandidates();
                if (candidates.Count > 0)
                {
                    var context = new RoutingContext(liveDimension, classification.IsUtility, candidates);
                    var selectedName = await _routingPolicy.SelectModelAsync(context, cancellationToken);

                    // Validate that the policy returned a model from the eligible candidate set to enforce contract.
                    var selectedInCandidates = candidates.Any(c => c.ModelName == selectedName);
                    if (!selectedInCandidates)
                    {
                        _logger.LogWarning(
                            "[INTERCEPTOR] Routing policy selected ineligible model '{Model}' not in candidate set; falling back to memory ranking.",
                            SanitizeForLog(selectedName));
                    }
                    else if (_modelRouteResolver.TryResolve(selectedName, out var selectedRoute))
                    {
                        // docs/router/utility-model-routing.md §B5: the routing decision itself (dimension,
                        // isUtility, chosen model), logged through Serilog so it reaches the same telemetry
                        // pipeline as every other structured routing log line (Program.cs's TelemetryLogEventSink
                        // sink forwards every log event to connected dashboards, not just RoutingTelemetryEvent).
                        _logger.LogInformation(
                            "[INTERCEPTOR] Routing policy selected '{Model}' for dimension '{Dimension}' (isUtility={IsUtility}).",
                            SanitizeForLog(selectedName),
                            SanitizeForLog(liveDimension),
                            classification.IsUtility);
                        return selectedRoute;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[INTERCEPTOR] Routing policy selected unresolvable model '{Model}'; falling back to memory ranking.",
                            SanitizeForLog(selectedName));
                    }
                }
            }

            return RankEligibleModels([], liveDimension).FirstOrDefault();
        }

        /// <summary>
        /// Builds one <see cref="RoutingCandidate"/> per currently-eligible model (the same eligibility
        /// rules <see cref="GetEligibleRoutes"/> applies for <see cref="RankEligibleModels"/>), for handing
        /// to an <see cref="IRoutingPolicy"/>.
        /// </summary>
        private List<RoutingCandidate> BuildRoutingCandidates() =>
            GetEligibleRoutes([])
                .Select(e => new RoutingCandidate(e.Route.ModelName, e.Route.Provider, e.Route.IsFree))
                .ToList();

        /// <summary>
        /// Ranks every currently-configured model other than <paramref name="excludeModelNames"/>, whose
        /// upstream target isn't presently open per <see cref="ICircuitBreaker.IsOpen"/>, whose provider
        /// isn't presently open per <see cref="ICircuitBreaker.IsProviderOpen"/> (both read-only checks -
        /// building this list never itself claims a half-open probe slot; that's reserved for
        /// <see cref="ICircuitBreaker.ShouldBypass"/>/<see cref="ICircuitBreaker.ShouldBypassProvider"/>,
        /// called by <see cref="ProxyMiddleware"/> immediately before it actually attempts a candidate), and
        /// whose provider hasn't been switched off via Governance &gt; Providers' Stop control (see
        /// <see cref="IModelRouteResolver.IsProviderEnabled"/>), and whose own Start/Stop toggle or last
        /// endpoint scan hasn't stopped it (see <see cref="IModelRouteResolver.IsModelEnabled"/>), by
        /// <see cref="RouterMemory.GetAverageScore"/> under <paramref name="liveDimension"/>, descending. A
        /// candidate with no recorded score yet is treated as <see cref="ColdStartRankingScore"/> rather
        /// than assumed worst, so cold-start candidates interleave with scored ones instead of always
        /// sinking to the bottom; ties preserve <see cref="IModelRouteResolver.ListModels"/>'s configured
        /// order (LINQ's <c>OrderByDescending</c> is a stable sort).
        /// </summary>
        /// <param name="excludeModelNames">Model names to omit from the ranking (e.g. the primary already queued).</param>
        /// <param name="liveDimension">The request's inferred live dimension.</param>
        private List<ResolvedModelRoute> RankEligibleModels(IReadOnlyCollection<string> excludeModelNames, string liveDimension) =>
            GetEligibleRoutes(excludeModelNames)
                .OrderByDescending(e => _routerMemory?.GetAverageScore(liveDimension, e.ModelName) ?? ColdStartRankingScore)
                .Select(e => e.Route)
                .ToList();

        /// <summary>
        /// The shared eligibility filter <see cref="RankEligibleModels"/> and <see cref="BuildRoutingCandidates"/>
        /// both build on: every currently-configured model other than <paramref name="excludeModelNames"/>,
        /// excluding any whose upstream target or provider is presently circuit-open, or whose provider or
        /// model has been administratively disabled. See <see cref="RankEligibleModels"/>'s remarks for the
        /// full rationale behind each check.
        /// </summary>
        /// <param name="excludeModelNames">Model names to omit from the result.</param>
        private List<(string ModelName, ResolvedModelRoute Route)> GetEligibleRoutes(IReadOnlyCollection<string> excludeModelNames)
        {
            var excluded = new HashSet<string>(excludeModelNames, StringComparer.OrdinalIgnoreCase);
            var eligible = new List<(string ModelName, ResolvedModelRoute Route)>();

            foreach (var candidate in _modelRouteResolver.ListModels())
            {
                if (excluded.Contains(candidate.ModelName))
                {
                    continue;
                }

                if (!_modelRouteResolver.TryResolve(candidate.ModelName, out var candidateRoute))
                {
                    continue;
                }

                if (_circuitBreaker.IsOpen(CircuitBreakerTargetKey.FromRoute(candidateRoute)) ||
                    _circuitBreaker.IsProviderOpen(candidateRoute.Provider) ||
                    !_modelRouteResolver.IsProviderEnabled(candidateRoute.Provider) ||
                    !_modelRouteResolver.IsModelEnabled(candidate.ModelName))
                {
                    continue;
                }

                eligible.Add((candidate.ModelName, candidateRoute));
            }

            return eligible;
        }

        /// <summary>
        /// Strips CR/LF from a client-controlled value (request method, scheme, host, path, or the
        /// requested model name from the body) before it is placed in a log message template. Without this,
        /// a crafted value could inject newlines into a text log sink and forge additional, fabricated log
        /// entries (CodeQL: "Log entries created from user input" / log forging, CWE-117). Chained
        /// <see cref="string.Replace(string, string)"/> calls directly on the tainted value is the sanitizer
        /// shape CodeQL's data-flow analysis recognizes as breaking the taint path - mirrors
        /// <see cref="ProxyMiddleware"/>'s own <c>SanitizeForLog</c>.
        /// </summary>
        private static string SanitizeForLog(string? value) =>
            value?.Replace("\r", " ").Replace("\n", " ") ?? string.Empty;

        /// <summary>
        /// Rewrites the request body's <c>model</c> field to the given route's upstream model id and
        /// serializes it, producing one failover candidate. Reuses (and mutates) <paramref name="jsonObject"/>
        /// in place - callers invoke this sequentially per candidate, and only the serialized snapshot each
        /// call returns is retained, so the shared node's transient state between calls is never observed.
        /// </summary>
        private static RouteCandidate BuildCandidate(JsonObject jsonObject, ResolvedModelRoute route)
        {
            jsonObject["model"] = route.ProviderModelId;
            var rewrittenBody = Encoding.UTF8.GetBytes(jsonObject.ToJsonString());
            return new RouteCandidate(
                route,
                rewrittenBody,
                CarriesTools(jsonObject),
                CarriesToolHistory(jsonObject),
                CarriesResponseFormat(jsonObject));
        }

        /// <summary>
        /// Whether this request offers the model any tools at all - the gate on installing tool-call
        /// normalization downstream (<c>docs/router/tool-call-normalization.md</c> §3.4 performance rule 1).
        /// Read from the body this method has already parsed rather than re-parsing it, which is what makes
        /// the check free.
        /// </summary>
        /// <remarks>
        /// An empty <c>tools</c> array counts as no tools: the model was offered nothing, so any tool-call
        /// syntax in its reply is prose about tool calling, not an invocation - exactly the false positive
        /// per-model arming exists to avoid.
        /// </remarks>
        private static bool CarriesTools(JsonObject jsonObject) =>
            jsonObject["tools"] is JsonArray { Count: > 0 };

        /// <summary>
        /// Whether the client set its own <c>response_format</c>, which makes constrained tool calling
        /// unavailable for this request - see <see cref="RouteCandidate.CarriesResponseFormat"/>.
        /// </summary>
        /// <remarks>
        /// Any non-null value counts, including a shape this build does not recognize. The question is not
        /// "did the client ask for something we understand" but "would setting our own overwrite theirs",
        /// and the answer to that is yes for every value they could have sent.
        /// </remarks>
        private static bool CarriesResponseFormat(JsonObject jsonObject) =>
            jsonObject["response_format"] is not null;

        /// <summary>
        /// Whether the conversation already contains tool-calling turns, which an emulated model's chat
        /// template cannot render (<c>docs/router/tool-call-normalization.md</c> Phase 5). Read from the
        /// same already-parsed body as <see cref="CarriesTools"/>.
        /// </summary>
        /// <remarks>
        /// Stops at the first match rather than surveying the whole conversation: the answer is a single
        /// bool, and a long chat's message list is the largest thing in the request body. Both shapes are
        /// checked because either alone is enough to confuse the model - an assistant turn it cannot read
        /// as its own, or a result whose role its template has never seen.
        /// </remarks>
        private static bool CarriesToolHistory(JsonObject jsonObject)
        {
            if (jsonObject["messages"] is not JsonArray messages)
            {
                return false;
            }

            foreach (var node in messages)
            {
                if (node is not JsonObject message)
                {
                    continue;
                }

                if (message["tool_calls"] is JsonArray { Count: > 0 })
                {
                    return true;
                }

                if (message["role"] is JsonValue role &&
                    role.TryGetValue<string>(out var value) &&
                    string.Equals(value, "tool", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

