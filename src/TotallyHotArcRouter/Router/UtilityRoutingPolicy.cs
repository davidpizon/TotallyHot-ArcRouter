using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Cost-aware, quality-gated <see cref="IRoutingPolicy"/> for utility/background traffic
/// (<c>docs/router/utility-model-routing.md</c> §B3), selecting <c>argmax r = ε₁·s + ε₂·κ</c> over
/// candidates that pass the quality gate.
/// </summary>
/// <remarks>
/// A candidate's quality term <c>s</c> defaults to <c>0</c> in the reward when
/// <see cref="RouterMemory.GetAverageScore"/> has no observation yet - deliberately not a special-cased
/// cold-start branch. With every candidate's <c>s</c> at <c>0</c>, the reward collapses to ranking by
/// <c>ε₂·κ</c> alone, i.e. cheapest-first, exactly matching §B3.6's cold-start requirement; as real scores
/// accumulate, <c>s</c> takes over the ranking with no code-path switch. The quality <em>gate</em> (excluding
/// a candidate scored below <see cref="RoutingOptions.UtilityMinQualityScore"/>) is a separate concern from
/// this reward default and only ever fires for candidates that have been observed - an unobserved candidate
/// is never gate-dropped (§B3.4).
/// <para>
/// Per §B3a, this policy uses <see cref="PriceContext.Standard"/> for every candidate: nothing upstream of
/// selection currently computes whether a request is batch-eligible or repeats cached context, so
/// tier-aware pricing is deferred until a caller can supply a real <see cref="PriceContext"/>.
/// </para>
/// <para>
/// Exploration (occasionally sampling an unobserved-but-priced candidate outside the argmax) is not
/// implemented here - the manuscript's <see cref="RoutingOptions.EnableExploration"/>/
/// <see cref="RoutingOptions.ExplorationRate"/> knobs remain <see cref="AgentAsARouter"/>'s concern for the
/// general path; extending them to the utility path is unscoped pending real utility traffic to tune
/// against (documented deferral, <c>AGENTS.md</c>'s "Settled deferrals" convention).
/// </para>
/// </remarks>
public sealed class UtilityRoutingPolicy : IRoutingPolicy
{
    /// <summary>
    /// The price catalog's D1 freshness floor for a routing decision
    /// (<c>docs/router/model-price-catalog.md</c>): a price older than this is treated as unpriced rather
    /// than trusted for cost ranking.
    /// </summary>
    private static readonly TimeSpan PriceFreshnessFloor = TimeSpan.FromHours(24);

    private readonly IModelPriceCatalog _priceCatalog;
    private readonly RouterMemory _memory;
    private readonly RoutingOptions _options;
    private readonly ILogger<UtilityRoutingPolicy> _logger;

    /// <param name="priceCatalog">Supplies the cost term κ, gated by <see cref="PriceFreshnessFloor"/>.</param>
    /// <param name="memory">Supplies the quality term s via <see cref="RouterMemory.GetAverageScore"/>.</param>
    /// <param name="options">Carries ε₁, ε₂, and <see cref="RoutingOptions.UtilityMinQualityScore"/>.</param>
    /// <param name="logger">Used to log the rare all-candidates-excluded degradation.</param>
    public UtilityRoutingPolicy(
        IModelPriceCatalog priceCatalog,
        RouterMemory memory,
        IOptions<RoutingOptions> options,
        ILogger<UtilityRoutingPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(priceCatalog);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _priceCatalog = priceCatalog;
        _memory = memory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Candidates.Count == 0)
        {
            throw new InvalidOperationException("UtilityRoutingPolicy requires at least one candidate.");
        }

        var scored = context.Candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Quality = _memory.GetAverageScore(context.Dimension, candidate.ModelName),
                Cost = ResolveCost(candidate)
            })
            .ToList();

        var priced = scored
            // Unpriced: excluded from cost ranking entirely - it cannot be compared, not treated as free.
            .Where(x => x.Cost.HasValue)
            .ToList();

        var gated = priced
            // The quality gate (§B3.4): only an *observed* score below the floor is excluded.
            .Where(x => x.Quality is not double q || q >= _options.UtilityMinQualityScore)
            .ToList();

        // Best case: candidates that pass both pricing and quality gate.
        if (gated.Count > 0)
        {
            var selected = gated
                .OrderByDescending(x => (_options.Epsilon1 * (x.Quality ?? 0)) + (_options.Epsilon2 * (double)x.Cost!.Value))
                .First();

            return Task.FromResult(selected.Candidate.ModelName);
        }

        // Degradation case 1: candidates with pricing but failed quality gate; pick cheapest.
        if (priced.Count > 0)
        {
            var fallback = priced.OrderBy(x => x.Cost).First().Candidate.ModelName;
            _logger.LogWarning(
                "All utility candidates failed quality gate for dimension '{Dimension}'; falling back to cheapest priced model '{Model}'.",
                context.Dimension,
                fallback);
            return Task.FromResult(fallback);
        }

        // Degradation case 2: no candidates have pricing; pick by observed quality (still gated), with a
        // deterministic tie-break so the outcome never depends on input ordering.
        var qualityGated = scored
            .Where(x => x.Quality is not double q || q >= _options.UtilityMinQualityScore)
            .ToList();
        var fallbackCandidates = qualityGated.Count > 0 ? qualityGated : scored;

        var unpricedFallback = fallbackCandidates
            .OrderByDescending(x => x.Quality ?? double.MinValue)
            .ThenBy(x => x.Candidate.ModelName, StringComparer.Ordinal)
            .First()
            .Candidate.ModelName;

        _logger.LogError(
            "No utility candidates have pricing for dimension '{Dimension}'; falling back to best-observed-quality candidate '{Model}'.",
            context.Dimension,
            unpricedFallback);
        return Task.FromResult(unpricedFallback);
    }

    /// <summary>
    /// Resolves candidate's blended cost in USD per 1,000,000 tokens (average of input and output rates),
    /// or <see langword="null"/> when it is unpriced — no fresh catalog row, and not flagged
    /// <see cref="RoutingCandidate.IsFree"/>.
    /// </summary>
    private decimal? ResolveCost(RoutingCandidate candidate)
    {
        if (candidate.IsFree)
        {
            return 0m;
        }

        var key = new ModelKey(candidate.ModelName, candidate.Provider);
        var price = _priceCatalog.GetFreshPriceForRouting(key, PriceContext.Standard, PriceFreshnessFloor);
        return price is null ? null : (price.InputPerMillionTokens + price.OutputPerMillionTokens) / 2m;
    }
}
