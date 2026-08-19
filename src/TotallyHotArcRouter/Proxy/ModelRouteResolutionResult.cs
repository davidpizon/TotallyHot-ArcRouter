using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// A single upstream this request may be forwarded to: a resolved route plus the request body already
/// rewritten with that route's <c>model</c> id. The first candidate is the client's requested model (or its
/// circuit-breaker substitute, see <c>RequestInterceptor.ResolveModelRouteAsync</c>); any further candidates
/// are other currently-eligible configured models, ranked by <c>RouterMemory</c> score - the dynamic
/// replacement for the old static per-model <c>Fallbacks</c> list (see
/// <c>docs/router/agent-resilience-strategies.md</c>). Each candidate carries its own rewritten body because
/// a backup on a different provider needs a different <c>model</c> value (and, downstream, a different
/// payload translator).
/// </summary>
/// <param name="Route">The resolved upstream route for this candidate.</param>
/// <param name="RewrittenBody">The request body with <c>model</c> rewritten to <see cref="ResolvedModelRoute.ProviderModelId"/>.</param>
/// <param name="CarriesTools">
/// Whether the client's request body carried a non-empty <c>tools</c> array. Read off the
/// <c>JsonObject</c> already parsed to rewrite <c>model</c>, so it costs nothing beyond the check itself -
/// which is the point: it is the gate that keeps tool-call normalization off the overwhelming majority of
/// responses, which were never going to contain a tool call because no tool was offered
/// (<c>docs/router/tool-call-normalization.md</c> §3.4 performance rule 1). Identical across every
/// candidate for a request - each candidate rewrites only <c>model</c> - but carried per candidate because
/// that is where the forwarding path reads it.
/// <para>
/// Deliberately has <b>no default</b>. A defaulted <see langword="false"/> would let a construction site
/// silently opt a tools-carrying request out of normalization by omission - a failure that is invisible at
/// the call site and shows up only as the original bug reappearing. Making it required means the compiler
/// asks the question at every site instead.
/// </para>
/// </param>
/// <param name="CarriesToolHistory">
/// Whether the conversation already contains tool-calling turns - a <c>role: "tool"</c> result, or an
/// assistant message carrying <c>tool_calls</c>. Separate from <paramref name="CarriesTools"/> because the
/// two answer different questions: that one asks whether the model is being offered tools *now*, this one
/// asks whether the history contains shapes an emulated model cannot read
/// (<c>docs/router/tool-call-normalization.md</c> Phase 5). A follow-up turn normally carries both, but a
/// client that resends history without re-offering the tools would otherwise forward <c>role: "tool"</c>
/// straight to a model whose chat template has no such role - which is the "emulation works for exactly
/// one turn" failure the phase exists to avoid.
/// <para>
/// Defaulted, unlike <paramref name="CarriesTools"/>, because omitting it cannot resurrect the original
/// bug: the worst case is that a history-only follow-up is forwarded exactly as it is today.
/// </para>
/// </param>
/// <param name="CarriesResponseFormat">
/// Whether the client set its own <c>response_format</c>. Read from the same already-parsed body, and
/// consulted only to <em>decline</em> constrained tool calling
/// (<c>docs/router/tool-call-normalization.md</c>): that mode works by setting <c>response_format</c> to a
/// tool-call envelope schema, and a request can only carry one. The client's wins, because silently
/// replacing a caller's structured-output contract with TotallyHot.ArcRouter's own would corrupt a response the
/// caller is about to parse - a worse failure than the unreliable tool calling it was meant to fix.
/// <para>
/// Defaulted for the same reason as <paramref name="CarriesToolHistory"/>: omitting it cannot resurrect the
/// original bug, it only forgoes an optimization. Reading <see langword="false"/> when the client did send
/// one is the harmful direction, and that requires actively passing the wrong value rather than omitting it.
/// </para>
/// </param>
public sealed record RouteCandidate(
    ResolvedModelRoute Route,
    byte[] RewrittenBody,
    bool CarriesTools,
    bool CarriesToolHistory = false,
    bool CarriesResponseFormat = false);

/// <summary>
/// The outcome of resolving and rewriting a request body against the known-model allowlist.
/// </summary>
public sealed record ModelRouteResolutionResult
{
    /// <summary>
    /// Sets every backing field directly; only reached through the <see cref="Success"/>/<see cref="Failure"/>
    /// factories so each call site is forced to state whether the resolution succeeded and what it produced.
    /// </summary>
    /// <param name="isSuccess">See <see cref="IsSuccess"/>.</param>
    /// <param name="candidates">See <see cref="Candidates"/>; <see langword="null"/> becomes an empty list.</param>
    /// <param name="errorMessage">See <see cref="ErrorMessage"/>.</param>
    /// <param name="requestedModelName">See <see cref="RequestedModelName"/>.</param>
    /// <param name="substitutionReason">See <see cref="SubstitutionReason"/>.</param>
    /// <param name="taskEmbedding">See <see cref="TaskEmbedding"/>.</param>
    /// <param name="routerTokens">See <see cref="RouterTokens"/>.</param>
    private ModelRouteResolutionResult(bool isSuccess, IReadOnlyList<RouteCandidate>? candidates, string? errorMessage, string? requestedModelName, RoutingSubstitutionReason substitutionReason, float[]? taskEmbedding, int routerTokens)
    {
        IsSuccess = isSuccess;
        Candidates = candidates ?? [];
        ErrorMessage = errorMessage;
        RequestedModelName = requestedModelName;
        SubstitutionReason = substitutionReason;
        TaskEmbedding = taskEmbedding;
        RouterTokens = routerTokens;
    }

    /// <summary>
    /// Gets a value indicating whether the request's model was resolved to a known upstream.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the ordered upstreams to try: the requested model first, then its fallbacks (see
    /// <see cref="RouteCandidate"/>), when <see cref="IsSuccess"/> is <see langword="true"/>. Never empty
    /// on success. A model with no configured fallbacks yields a single-element list.
    /// </summary>
    public IReadOnlyList<RouteCandidate> Candidates { get; }

    /// <summary>
    /// Gets the resolved upstream route for the client's requested model (the first candidate), when
    /// <see cref="IsSuccess"/> is <see langword="true"/>. Convenience accessor for the common no-fallback
    /// path and for callers that only care about the primary route.
    /// </summary>
    public ResolvedModelRoute? Route => IsSuccess ? Candidates[0].Route : null;

    /// <summary>
    /// Gets the primary request body with <c>model</c> rewritten to the provider's model id (the first
    /// candidate's body), when <see cref="IsSuccess"/> is <see langword="true"/>.
    /// </summary>
    public byte[]? RewrittenBody => IsSuccess ? Candidates[0].RewrittenBody : null;

    /// <summary>
    /// Gets a human-readable reason for failure, when <see cref="IsSuccess"/> is <see langword="false"/>.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Gets the client's literal <c>model</c> string from the request body, when <see cref="IsSuccess"/>
    /// is <see langword="true"/> - captured before single-model-serving's forced override (see
    /// <c>RequestInterceptor.ResolveModelRouteAsync</c>), so it always reflects what the client actually
    /// asked for, not what was ultimately served. Distinct from <see cref="Route"/>'s
    /// <c>ResolvedModelRoute.ModelName</c>, which is the router's client-facing name for whichever
    /// candidate was lined up first, and from the eventually-served model's own name once
    /// <c>ProxyMiddleware</c>'s failover loop runs - see <c>docs/router/orchestrator-live-path-plan.md</c>
    /// §M2.2's three-field table.
    /// </summary>
    public string? RequestedModelName { get; }

    /// <summary>
    /// Gets why the primary candidate (<see cref="Route"/>) differs from <see cref="RequestedModelName"/>,
    /// as known at resolution time - <see cref="RoutingSubstitutionReason.None"/> when it doesn't.
    /// <c>ProxyMiddleware</c> may still report <see cref="RoutingSubstitutionReason.Failover"/> instead of
    /// this value when the primary candidate itself failed at the transport layer and a later one served,
    /// since that outcome isn't knowable at resolution time.
    /// </summary>
    public RoutingSubstitutionReason SubstitutionReason { get; }

    /// <summary>
    /// Gets the request's task embedding, computed on the request path under a time budget
    /// (docs/router/live-feedback-learning-plan.md Phase 2b), or <see langword="null"/> when it was not
    /// computed (no embedding client configured, still warming up, budget exceeded, or the request had no
    /// extractable text). Carried through so <c>ProxyMiddleware</c> can hand it to
    /// <c>Router.Embeddings.PendingTaskEmbeddingCache</c> once the request's correlation id is known -
    /// <see cref="TotallyHot.ArcRouter.Proxy.RequestInterceptor.ResolveModelRouteAsync"/> computes the
    /// embedding but does not itself know the correlation id (it is only assigned later, alongside session
    /// and turn resolution).
    /// </summary>
    public float[]? TaskEmbedding { get; }

    /// <summary>
    /// Gets the tokens the router consumed resolving this request - the embedding model's sequence length
    /// when <see cref="TaskEmbedding"/> was computed, and <c>0</c> when it was not. Carried alongside the
    /// embedding for the same reason: <c>ProxyMiddleware</c> is where telemetry is published, and this is
    /// the only path from the point the cost is incurred to the point it can be reported. Charging it into
    /// the request's telemetry is what lets a savings figure be net rather than gross (research-doc §5.1's
    /// <c>TotTok</c> = router + model).
    /// </summary>
    public int RouterTokens { get; }

    // A single-route Success(route, rewrittenBody) overload used to sit here. It was unreachable - every
    // caller goes through the candidate-list overload below, because even a no-fallback resolution builds
    // its one candidate through RequestInterceptor.BuildCandidate - and it constructed a RouteCandidate
    // without stating CarriesTools, so anything that did start calling it would have silently disabled
    // tool-call normalization for every tools-carrying request. Deleted rather than fixed: a convenience
    // overload nobody uses is not worth the second place to get this wrong.

    /// <summary>
    /// Creates a successful resolution result from an ordered candidate list (primary first, then fallbacks).
    /// </summary>
    /// <param name="candidates">The ordered candidate list; must contain at least the primary route.</param>
    /// <param name="requestedModelName">See <see cref="RequestedModelName"/>. No default - a silently-omitted client-literal name is worse than a compile error, same reasoning <see cref="RouteCandidate.CarriesTools"/> already applies.</param>
    /// <param name="substitutionReason">See <see cref="SubstitutionReason"/>.</param>
    /// <param name="taskEmbedding">See <see cref="TaskEmbedding"/>.</param>
    /// <param name="routerTokens">See <see cref="RouterTokens"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="candidates"/> is empty - a success must have at least the primary route, since <see cref="Route"/>/<see cref="RewrittenBody"/> index the first candidate.</exception>
    public static ModelRouteResolutionResult Success(IReadOnlyList<RouteCandidate> candidates, string requestedModelName, RoutingSubstitutionReason substitutionReason, float[]? taskEmbedding = null, int routerTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("A successful resolution must contain at least the primary route candidate.", nameof(candidates));
        }

        return new(true, candidates, null, requestedModelName, substitutionReason, taskEmbedding, routerTokens);
    }

    /// <summary>
    /// Creates a failed resolution result.
    /// </summary>
    /// <remarks>
    /// Reports zero router tokens even though resolution may have embedded the request before failing: a
    /// failed resolution never reaches <c>ProxyMiddleware.PublishTelemetryAsync</c>, so there is no event
    /// for the figure to appear on and no savings denominator it could distort.
    /// </remarks>
    public static ModelRouteResolutionResult Failure(string errorMessage) =>
        new(false, null, errorMessage, null, RoutingSubstitutionReason.None, null, 0);
}

