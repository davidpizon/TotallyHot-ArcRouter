using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Proxy
{
    /// <summary>
    /// Builds the ordered failover candidate list <see cref="RequestInterceptor.ResolveModelRouteAsync"/>
    /// hands to <see cref="ProxyMiddleware"/>, and owns the circuit-breaker eligibility ranking that list
    /// (and <see cref="RequestInterceptor"/>'s <see cref="IRoutingPolicy"/> candidate set) is built from.
    /// Split out of <see cref="RequestInterceptor"/> because this is the one piece of that class's routing
    /// logic that is entirely a function of the resolved primary route plus its own DI-injected
    /// collaborators - unlike forced single-model serving or routing-gate/auto-select resolution, which
    /// need <see cref="RequestInterceptor"/>'s own state (<c>_forcedModelName</c>, <c>_routingPolicy</c>)
    /// and stay there. Mirrors <see cref="TotallyHot.ArcRouter.Router.Orchestrator.IRoutingVoter"/>'s
    /// shape - stateless per call apart from its constructor-injected collaborators, and never throws - even
    /// though it isn't itself a voter: it builds and ranks the circuit-breaker-eligible candidate list
    /// rather than scoring one already-built request against <see cref="RouterMemory"/>.
    /// </summary>
    /// <param name="circuitBreaker">
    /// Per-upstream-target and per-provider trip state (<c>docs/router/agent-resilience-strategies.md</c>).
    /// Must be the same DI singleton instance <see cref="RequestInterceptor"/> and <see cref="ProxyMiddleware"/>
    /// share, since <see cref="ProxyMiddleware"/> is what records the successes/failures read back here.
    /// </param>
    /// <param name="modelRouteResolver">The known-model allowlist/resolver.</param>
    /// <param name="routerMemory">
    /// Optional score memory consulted when ranking a substitute/fallback candidate. <see langword="null"/>
    /// disables score-based ranking (every candidate is treated as cold-start).
    /// </param>
    /// <param name="interactionStatusStore">
    /// Optional per-provider admin-action/live-traffic status store
    /// (docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md,
    /// docs/adr/0005-protect-explicit-provider-selections-from-silent-substitution-on-any-circuit-
    /// trip.md), consulted when an explicit selection's target or provider is already circuit-open to
    /// synthesize a truthful client-facing message instead of silently substituting. Defaults to
    /// <see langword="null"/> - behaviorally inert (falls back to a generic message) when omitted.
    /// </param>
    /// <param name="logger">The logger, shared with the owning <see cref="RequestInterceptor"/>.</param>
    internal sealed class RoutingCandidateBuilder(
        ICircuitBreaker circuitBreaker,
        IModelRouteResolver modelRouteResolver,
        RouterMemory? routerMemory,
        IProviderInteractionStatusStore? interactionStatusStore,
        ILogger logger)
    {
        /// <summary>
        /// The neutral prior assigned to a candidate with no recorded <see cref="RouterMemory"/> score yet
        /// (cold start), used when ranking fallback/substitute candidates. Cold-start candidates therefore
        /// interleave with scored ones (score range 0.0-1.0) instead of always sinking to the bottom or
        /// jumping to the top.
        /// </summary>
        internal const double ColdStartRankingScore = 0.5;

        /// <summary>
        /// Builds the ordered candidate list for an already-resolved (not forced-single-model) primary
        /// route: substitutes it for the next-best eligible model when its circuit is open or it has been
        /// administratively stopped and the request is not an explicit selection, or - for an explicit
        /// selection whose target/provider is already circuit-open - leaves it unsubstituted and instead
        /// reports a truthful client-facing message via <see cref="RoutingCandidateBuildResult.ExplicitCircuitTripBlockMessage"/>
        /// (docs/adr/0004-.../0005-...). Then appends every other currently-eligible configured model,
        /// deduplicated by upstream target and ranked by <see cref="RouterMemory"/> score, as further
        /// failover hops.
        /// </summary>
        /// <param name="jsonObject">The already-parsed, mutable request body to rewrite per candidate.</param>
        /// <param name="route">The primary route resolved before circuit-breaker/administrative checks.</param>
        /// <param name="substitutionReasonSoFar">
        /// <see cref="RoutingSubstitutionReason"/> as known before this call - <see cref="RoutingSubstitutionReason.None"/>
        /// means the request named this route explicitly; anything else means it was already auto-selected
        /// or substituted, so the ADR-0004/0005 explicit-selection protection does not apply.
        /// </param>
        /// <param name="liveDimension">The request's live dimension key for score lookup.</param>
        /// <returns>The ordered candidate list plus the (possibly substituted) route and updated substitution state.</returns>
        public RoutingCandidateBuildResult Build(
            JsonObject jsonObject,
            ResolvedModelRoute route,
            RoutingSubstitutionReason substitutionReasonSoFar,
            string liveDimension)
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
            //
            // docs/adr/0004-.../0005-...: this substitution is for an auto-selected/already-substituted
            // request only. An explicit selection (substitutionReasonSoFar still None at this point) whose
            // target or provider is already circuit-open is never silently substituted for that reason -
            // it is relayed the truth instead, via ExplicitCircuitTripBlockMessage below - regardless of
            // whether the trip is target-level or provider-wide. Only providerStopped/modelStopped (an
            // operator's own Stop/disable, not an outage) still substitutes for an explicit selection.
            var targetOpen = circuitBreaker.IsOpen(CircuitBreakerTargetKey.FromRoute(route));
            var providerOpen = circuitBreaker.IsProviderOpen(route.Provider);
            var providerStopped = !modelRouteResolver.IsProviderEnabled(route.Provider);
            var modelStopped = !modelRouteResolver.IsModelEnabled(route.ModelName);
            var isExplicitSoFar = substitutionReasonSoFar == RoutingSubstitutionReason.None;
            var substitutionReason = substitutionReasonSoFar;
            string? explicitCircuitTripBlockMessage = null;

            if ((targetOpen || providerOpen) && isExplicitSoFar)
            {
                if (providerOpen)
                {
                    // Three-way fallback: LiveTrafficStatus (hot-path-detected, e.g. out-of-credits) ->
                    // AdminActionStatus (e.g. a 401 first surfaced by "Refresh from endpoint") -> generic.
                    // Both tracks are per-provider, so this branch only applies when the whole provider
                    // is open.
                    var liveTraffic = interactionStatusStore?.GetLiveTraffic(route.Provider);
                    var adminAction = interactionStatusStore?.Get(route.Provider);
                    explicitCircuitTripBlockMessage = liveTraffic?.Message
                        ?? (adminAction is { Ok: false } ? adminAction.Message : null)
                        ?? "This provider is temporarily unavailable.";
                }
                else
                {
                    // Target-level only: no per-model interaction-status record exists to draw from
                    // (both tracks are per-provider), so this is always a generic, honest message -
                    // never borrows the provider-wide tracks' text, which could misattribute an
                    // unrelated cause to this one model.
                    explicitCircuitTripBlockMessage = $"Model '{route.ModelName}' is temporarily unavailable.";
                }

                logger.LogInformation(
                    "[INTERCEPTOR] Explicit selection '{ModelName}' is circuit-open; relaying the truth instead of substituting.",
                    SanitizeForLog(route.ModelName));
            }
            else if (targetOpen || providerOpen || providerStopped || modelStopped)
            {
                var substitute = RankEligibleModels([route.ModelName], liveDimension).FirstOrDefault();
                if (substitute is not null)
                {
                    logger.LogInformation(
                        "[INTERCEPTOR] Model '{ModelName}' circuit is open; substituting next-best model '{Substitute}'.",
                        SanitizeForLog(route.ModelName), SanitizeForLog(substitute.ModelName));
                    substitutionReason = targetOpen || providerOpen
                        ? RoutingSubstitutionReason.CircuitOpen
                        : RoutingSubstitutionReason.ModelStopped;
                    route = substitute;
                }
            }

            // Build the ordered list of upstreams to try: the (possibly substituted) primary, then every
            // other currently-eligible configured model ranked by RouterMemory score - the dynamic
            // replacement for the old static per-model Fallbacks list. Each candidate gets its own
            // rewritten body because a backup on a different provider needs a different upstream model
            // id substituted into the same request.
            var candidates = new List<RouteCandidate> { RequestBodyIntrospection.BuildCandidate(jsonObject, route) };

            var seenTargets = new HashSet<CircuitBreakerTargetKey> { CircuitBreakerTargetKey.FromRoute(route) };
            foreach (var fallbackRoute in RankEligibleModels([route.ModelName], liveDimension))
            {
                // Skip a candidate that resolves to the same upstream target (same provider, base URL,
                // and model id) as one already queued - a duplicate hop would just repeat the same
                // failing call. Keyed on the full target, not ProviderModelId alone, so two genuinely
                // distinct providers that happen to share a model-id string are both kept as valid hops.
                if (seenTargets.Add(CircuitBreakerTargetKey.FromRoute(fallbackRoute)))
                {
                    candidates.Add(RequestBodyIntrospection.BuildCandidate(jsonObject, fallbackRoute));
                }
            }

            return new RoutingCandidateBuildResult(candidates, route, substitutionReason, explicitCircuitTripBlockMessage);
        }

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
        public List<ResolvedModelRoute> RankEligibleModels(IReadOnlyCollection<string> excludeModelNames, string liveDimension) =>
            GetEligibleRoutes(excludeModelNames)
                .OrderByDescending(e => routerMemory?.GetAverageScore(liveDimension, e.ModelName) ?? ColdStartRankingScore)
                .Select(e => e.Route)
                .ToList();

        /// <summary>
        /// The shared eligibility filter <see cref="RankEligibleModels"/> and
        /// <see cref="RequestInterceptor"/>'s <c>BuildRoutingCandidates</c> both build on: every
        /// currently-configured model other than <paramref name="excludeModelNames"/>, excluding any whose
        /// upstream target or provider is presently circuit-open, or whose provider or model has been
        /// administratively disabled. See <see cref="RankEligibleModels"/>'s remarks for the full rationale
        /// behind each check.
        /// </summary>
        /// <param name="excludeModelNames">Model names to omit from the result.</param>
        public List<(string ModelName, ResolvedModelRoute Route)> GetEligibleRoutes(IReadOnlyCollection<string> excludeModelNames)
        {
            var excluded = new HashSet<string>(excludeModelNames, StringComparer.OrdinalIgnoreCase);
            var eligible = new List<(string ModelName, ResolvedModelRoute Route)>();

            foreach (var candidate in modelRouteResolver.ListModels())
            {
                if (excluded.Contains(candidate.ModelName))
                {
                    continue;
                }

                if (!modelRouteResolver.TryResolve(candidate.ModelName, out var candidateRoute))
                {
                    continue;
                }

                if (circuitBreaker.IsOpen(CircuitBreakerTargetKey.FromRoute(candidateRoute)) ||
                    circuitBreaker.IsProviderOpen(candidateRoute.Provider) ||
                    !modelRouteResolver.IsProviderEnabled(candidateRoute.Provider) ||
                    !modelRouteResolver.IsModelEnabled(candidate.ModelName))
                {
                    continue;
                }

                eligible.Add((candidate.ModelName, candidateRoute));
            }

            return eligible;
        }

        /// <summary>
        /// Strips CR/LF from a client-controlled value (here, a model or provider name drawn from the
        /// resolved route) before it is placed in a log message template. Without this, a crafted value
        /// could inject newlines into a text log sink and forge additional, fabricated log entries
        /// (CodeQL: "Log entries created from user input" / log forging, CWE-117). Chained
        /// <see cref="string.Replace(string, string)"/> calls directly on the tainted value is the sanitizer
        /// shape CodeQL's data-flow analysis recognizes as breaking the taint path - mirrors
        /// <see cref="RequestInterceptor"/>'s and <see cref="ProxyMiddleware"/>'s own <c>SanitizeForLog</c>.
        /// </summary>
        private static string SanitizeForLog(string? value) =>
            value?.Replace("\r", " ").Replace("\n", " ") ?? string.Empty;
    }

    /// <summary>
    /// The outcome of <see cref="RoutingCandidateBuilder.Build"/>: the ordered candidate list plus the
    /// (possibly substituted) route and its final <see cref="RoutingSubstitutionReason"/>, so
    /// <see cref="RequestInterceptor.ResolveModelRouteAsync"/> can carry them into
    /// <see cref="ModelRouteResolutionResult"/> without recomputing anything.
    /// </summary>
    /// <param name="Candidates">The ordered failover candidate list, primary first.</param>
    /// <param name="Route">The primary route actually used to build <paramref name="Candidates"/>[0] - the original route, or its circuit-breaker/administrative substitute.</param>
    /// <param name="SubstitutionReason">Why <paramref name="Route"/> differs from the route <see cref="RoutingCandidateBuilder.Build"/> was called with, or the reason passed in when it doesn't.</param>
    /// <param name="ExplicitCircuitTripBlockMessage">
    /// See <see cref="ModelRouteResolutionResult.ExplicitCircuitTripBlockMessage"/>. <see langword="null"/>
    /// unless this request explicitly named a model/provider whose circuit was already open.
    /// </param>
    internal sealed record RoutingCandidateBuildResult(
        List<RouteCandidate> Candidates,
        ResolvedModelRoute Route,
        RoutingSubstitutionReason SubstitutionReason,
        string? ExplicitCircuitTripBlockMessage);
}
