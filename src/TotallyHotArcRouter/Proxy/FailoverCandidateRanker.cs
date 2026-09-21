using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Orders the failover/substitute list <see cref="RoutingCandidateBuilder"/> hands to
/// <see cref="ProxyMiddleware"/> so a same-request retry walks the routing policy's ranked picks
/// rather than a separately memory-ranked menu. Extracted as a static class so the ranking rule can
/// be unit-tested without constructing <see cref="RequestInterceptor"/> or a live circuit breaker:
/// that rule is the completeness gap on provider-500 / circuit-open fall-through (the next voter pick
/// must be attempted, not whichever model <see cref="RouterMemory"/> happens to score highest).
/// </summary>
internal static class FailoverCandidateRanker
{
    /// <summary>
    /// Ranks currently-eligible routes for failover or circuit-open substitution. When
    /// <paramref name="policyScores"/> carries per-model aggregates from a
    /// <see cref="TotallyHot.ArcRouter.Models.RoutingDecision"/>, models the ensemble actually scored come first, in
    /// descending score order, so the next hop is the next voter pick. Models the ensemble did not
    /// score (and every model, when <paramref name="policyScores"/> is omitted) keep today's
    /// <see cref="RouterMemory"/> ranking - cold-start
    /// <see cref="RoutingCandidateBuilder.ColdStartRankingScore"/> when memory has no observation -
    /// so an explicit named-model request, or a policy that never published scores, is unchanged.
    /// </summary>
    /// <param name="eligible">
    /// Routes already filtered for circuit-open / disabled state; ranking never itself bypasses those
    /// gates.
    /// </param>
    /// <param name="policyScores">
    /// Per-model aggregates from <see cref="TotallyHot.ArcRouter.Models.RoutingDecision.CandidateScores"/>, or
    /// <see langword="null"/> when no policy was consulted. Lookup is by
    /// <see cref="ResolvedModelRoute.ModelName"/>, so per-voter breakdown keys
    /// (<c>voter:{name}:{model}</c>) are ignored rather than mistaken for a model named that way.
    /// </param>
    /// <param name="routerMemory">
    /// Optional score memory used when a model has no policy score. <see langword="null"/> treats
    /// every such model as cold-start.
    /// </param>
    /// <param name="liveDimension">The request's live dimension key for memory lookup.</param>
    /// <returns>The same routes, ordered for the candidate loop to walk.</returns>
    public static List<ResolvedModelRoute> Rank(
        IReadOnlyList<(string ModelName, ResolvedModelRoute Route)> eligible,
        IReadOnlyDictionary<string, double>? policyScores,
        RouterMemory? routerMemory,
        string liveDimension)
    {
        ArgumentNullException.ThrowIfNull(eligible);

        if (policyScores is { Count: > 0 })
            // Scored picks first (the ensemble's ranking), then anyone the voters skipped, ranked by
            // memory / cold-start. Name is the deterministic tie-break among equally-scored voter
            // picks, matching OrchestratorRoutingPolicy's own argmax. The memory-only branch below
            // deliberately does *not* add a name tie-break: RankEligibleModels historically preserves
            // ListModels order via LINQ's stable sort, and an explicit request must keep that.
            return [.. eligible
                .OrderByDescending(e => policyScores.ContainsKey(e.ModelName))
                .ThenByDescending(e => PolicyOrMemoryScore(e.ModelName, policyScores, routerMemory, liveDimension))
                .ThenBy(keySelector: e => e.ModelName, comparer: StringComparer.Ordinal)
                .Select(e => e.Route)];

        return [.. eligible
            .OrderByDescending(e =>
                routerMemory?.GetAverageScore(dimension: liveDimension, model: e.ModelName) ??
                RoutingCandidateBuilder.ColdStartRankingScore)
            .Select(e => e.Route)];
    }

    /// <summary>
    /// The score used to order one eligible model: the policy aggregate when the ensemble scored it,
    /// otherwise the memory / cold-start value the memory-only path already used.
    /// </summary>
    private static double PolicyOrMemoryScore(
        string modelName,
        IReadOnlyDictionary<string, double> policyScores,
        RouterMemory? routerMemory,
        string liveDimension)
    {
        if (policyScores.TryGetValue(key: modelName, value: out var policyScore)) return policyScore;

        return routerMemory?.GetAverageScore(dimension: liveDimension, model: modelName) ??
               RoutingCandidateBuilder.ColdStartRankingScore;
    }
}
