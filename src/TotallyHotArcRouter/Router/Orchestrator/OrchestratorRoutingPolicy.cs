using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The Orchestrator ensemble (PLAN.md Phase L, research-doc §3.3/A.1): a weighted vote across every
/// enabled <see cref="IRoutingVoter"/>, argmax over each candidate's aggregate weighted score.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not yet on the live path.</b> This class is a self-contained, DI-registered component
/// (<c>ServiceCollectionExtensions</c> registers it by concrete type) - it does not replace
/// <see cref="CompositeRoutingPolicy"/> as the registered <see cref="IRoutingPolicy"/>. Wiring the
/// Orchestrator into live traffic by default is PLAN.md Phase M's job, deliberately not this one's.
/// </para>
/// <para>
/// <b>Weighting scheme.</b> Each voter contributes <c>weight(voter) × vote.Confidence</c> to its picked
/// model's aggregate score when it does not abstain; the argmax over aggregates is the selection. The
/// per-voter weights (<see cref="RoutingOptions.DimBestVoterWeight"/>, etc.) default to the values that
/// reproduce research-doc §3.3's worked example exactly - <c>dim_best</c> = 0.9, <c>memory_kNN</c> = 0.57
/// (0.9 + 0.57 = 1.47, the doc's Kimi-K2.5 weighted score), <c>logreg</c> = 0.43 (the doc's GLM-5 score),
/// <c>llm_router</c> = 0.64 (the doc's MiniMax-M2.7 score). The research doc does not publish an
/// independent derivation for these four numbers beyond that one worked example, so this is a documented
/// implementation choice - a simple per-voter fixed weight, no confidence scaling beyond each voter's own
/// - rather than a value transcribed from the paper; both weights and enablement are configurable under
/// <c>Routing</c> for a future phase (or an operator) to retune against real regret numbers (PLAN.md
/// Phase N).
/// </para>
/// <para>
/// <b>Degrade path.</b> A voter that abstains (missing model artifact, missing embedding/text, or an
/// internal exception this class catches and logs) simply contributes nothing - the argmax runs over
/// whichever voters did vote. With <see cref="LlmRouterVoter"/> permanently abstaining in this phase, every
/// decision already exercises the "three-voter" degrade path PLAN.md Phase L requires. If every voter
/// abstains, this falls back to <see cref="RoutingDecision.CreateFallback"/> rather than throwing -
/// matching every other <see cref="IRoutingPolicy"/> in this codebase's "never hard-fail a routing
/// decision" convention.
/// </para>
/// <para>
/// <b>Vote-breakdown logging.</b> <see cref="RoutingDecision.CandidateScores"/> carries both the
/// per-model aggregate weighted scores (keyed by model name - what argmax runs over) and each individual
/// non-abstaining vote (keyed <c>"voter:{voterName}:{modelName}"</c> - PLAN.md Phase L's "log the full
/// vote breakdown: each voter's pick, each weighted score, the argmax"). <see cref="RoutingDecision.Confidence"/>
/// is a separate, normalized figure (the winning aggregate divided by the total weight of voters that
/// actually voted, clamped to <c>[0, 1]</c>) since <see cref="RoutingDecision"/>'s constructor requires
/// confidence in that range while an aggregate weighted score is unbounded.
/// </para>
/// </remarks>
public sealed class OrchestratorRoutingPolicy : IRoutingPolicy
{
    private const string VoterKeyPrefix = "voter:";

    private readonly IReadOnlyList<IRoutingVoter> _voters;
    private readonly RoutingOptions _options;
    private readonly ILogger<OrchestratorRoutingPolicy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrchestratorRoutingPolicy"/> class.
    /// </summary>
    /// <param name="voters">Every registered voter (see <see cref="VoterNames"/> for the recognized identities).</param>
    /// <param name="options">Per-voter weights and enablement, and the fallback default model.</param>
    /// <param name="logger">The logger.</param>
    public OrchestratorRoutingPolicy(
        IEnumerable<IRoutingVoter> voters,
        IOptions<RoutingOptions> options,
        ILogger<OrchestratorRoutingPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(voters);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _voters = [.. voters];
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to <see cref="DecideAsync"/> with no task embedding/text - <see cref="RoutingContext"/>
    /// does not carry either today (PLAN.md Phase L's stated scope). Callers who have a task embedding or
    /// text should call <see cref="DecideAsync"/> directly so <see cref="MemoryKnnVoter"/> and
    /// <see cref="LogRegVoter"/> can participate instead of abstaining.
    /// </remarks>
    public async Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decision = await DecideAsync(context, taskEmbedding: null, taskText: null, cancellationToken).ConfigureAwait(false);
        return decision.SelectedModel;
    }

    /// <summary>
    /// Runs every enabled voter and returns the full weighted-vote decision, including the vote breakdown -
    /// the entry point tests and any future caller that has a task embedding/text should use instead of
    /// <see cref="SelectModelAsync"/>.
    /// </summary>
    /// <param name="context">The dimension and eligible candidates to select from.</param>
    /// <param name="taskEmbedding">The task's embedding, if available, for <see cref="MemoryKnnVoter"/>.</param>
    /// <param name="taskText">The task's prompt text, if available, for <see cref="LogRegVoter"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="context"/> has no candidates.</exception>
    public async Task<RoutingDecision> DecideAsync(
        RoutingContext context,
        float[]? taskEmbedding,
        string? taskText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Candidates.Count == 0)
        {
            throw new ArgumentException("The routing context has no candidates to vote over.", nameof(context));
        }

        var votingContext = new VotingContext(context.Dimension, context.Candidates, taskEmbedding, taskText);

        var candidateScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var effectiveWeight = 0d;
        var participatingVoters = 0;

        foreach (var voter in _voters)
        {
            if (!IsVoterEnabled(voter.Name))
            {
                continue;
            }

            var vote = await CastVoteAsync(voter, votingContext, cancellationToken).ConfigureAwait(false);
            if (vote.IsAbstain)
            {
                _logger.LogInformation("[ORCHESTRATOR] Voter {Voter} abstained for dimension {Dimension}.", voter.Name, context.Dimension);
                continue;
            }

            if (string.IsNullOrWhiteSpace(vote.ModelName))
            {
                // ModelName is non-null (IsAbstain already returned false) but Canonicalize below throws
                // on whitespace - a misbehaving voter must degrade to an abstention here, not hard-fail
                // the whole decision.
                _logger.LogWarning(
                    "[ORCHESTRATOR] Voter {Voter} returned a blank model name for dimension {Dimension}; treating as an abstention.",
                    voter.Name,
                    context.Dimension);
                continue;
            }

            // Canonicalize tolerates cosmetic spelling differences (casing, "." vs "-" version
            // punctuation) but keeps a dated snapshot distinct from its rolling base model, so a voter
            // that picks a specific pinned release is never silently credited to a different one. Passing
            // candidate.Provider strips only that candidate's own provider prefix, so a legitimately
            // slashed model id (e.g. "meta-llama/llama-3.1") is never mistaken for an unprefixed one.
            var candidateMatch = context.Candidates.FirstOrDefault(candidate =>
                string.Equals(
                    ModelNameCanonicalizer.Canonicalize(candidate.ModelName, candidate.Provider),
                    ModelNameCanonicalizer.Canonicalize(vote.ModelName!, candidate.Provider),
                    StringComparison.Ordinal));
            if (candidateMatch is null)
            {
                _logger.LogWarning(
                    "[ORCHESTRATOR] Voter {Voter} picked {Model}, which is not among the current candidates for dimension {Dimension}; treating as an abstention.",
                    voter.Name,
                    vote.ModelName,
                    context.Dimension);
                continue;
            }

            // Canonicalize's output is a comparison key, not a name it would be safe to store or return -
            // use the matched candidate's own configured ModelName for every key written below so
            // CandidateScores stays consistent with context.Candidates and SelectedModel when enumerated.
            var canonicalModelName = candidateMatch.ModelName;

            if (!double.IsFinite(vote.Confidence))
            {
                // Math.Clamp does not sanitize NaN/±Infinity - a non-finite confidence would otherwise
                // poison contribution (and everything summed from it) with NaN.
                _logger.LogWarning(
                    "[ORCHESTRATOR] Voter {Voter} returned a non-finite confidence ({Confidence}) for dimension {Dimension}; treating as an abstention.",
                    voter.Name,
                    vote.Confidence,
                    context.Dimension);
                continue;
            }

            var weight = GetVoterWeight(voter.Name);
            if (weight <= 0d)
            {
                // A non-positive weight contributes nothing but would still count as a "participating"
                // voter below, letting an all-zero-weight configuration deterministically pick a model
                // via the tie-break with no effective ensemble weight behind it. Degrade to an abstention
                // instead so that configuration falls back to the default model like a fully-abstained one.
                _logger.LogInformation(
                    "[ORCHESTRATOR] Voter {Voter} has a non-positive weight ({Weight}); treating as an abstention.",
                    voter.Name,
                    weight);
                continue;
            }

            var contribution = weight * Math.Clamp(vote.Confidence, 0d, 1d);

            candidateScores[$"{VoterKeyPrefix}{voter.Name}:{canonicalModelName}"] = contribution;
            candidateScores[canonicalModelName] = candidateScores.GetValueOrDefault(canonicalModelName) + contribution;
            effectiveWeight += weight;
            participatingVoters++;

            _logger.LogInformation(
                "[ORCHESTRATOR] Voter {Voter} picked {Model} with confidence {Confidence} and weight {Weight} (contribution {Contribution}).",
                voter.Name,
                canonicalModelName,
                vote.Confidence,
                weight,
                contribution);
        }

        // Selecting winners straight out of candidateScores by excluding the "voter:" prefix would
        // wrongly exclude a legitimate candidate model whose own name happens to start with "voter:",
        // and OrderByDescending alone leaves ties resolved by dictionary enumeration order (non-
        // deterministic). Restricting the search to context.Candidates and adding a deterministic
        // tie-break by model name avoids both.
        var best = context.Candidates
            .Select(candidate => (
                Model: candidate.ModelName,
                Score: candidateScores.TryGetValue(candidate.ModelName, out var score) ? (double?)score : null))
            .Where(entry => entry.Score is not null)
            .OrderByDescending(entry => entry.Score!.Value)
            .ThenBy(entry => entry.Model, StringComparer.Ordinal)
            .FirstOrDefault();

        if (participatingVoters == 0 || best.Model is null)
        {
            _logger.LogWarning(
                "[ORCHESTRATOR] Every voter abstained for dimension {Dimension}; falling back to the default model.",
                context.Dimension);
            return RoutingDecision.CreateFallback(_options.DefaultModel);
        }

        var confidence = effectiveWeight > 0 ? Math.Clamp(best.Score!.Value / effectiveWeight, 0d, 1d) : 0d;
        var decision = new RoutingDecision(
            best.Model,
            confidence,
            $"Orchestrator ensemble selected '{best.Model}' with weighted score {best.Score:F2} across {participatingVoters} voting voter(s).",
            DateTimeOffset.UtcNow,
            candidateScores);

        _logger.LogInformation(
            "[ORCHESTRATOR] Selected {Model} for dimension {Dimension} with weighted score {Score} (confidence {Confidence}, {VoterCount} voters participated).",
            decision.SelectedModel,
            context.Dimension,
            best.Score,
            decision.Confidence,
            participatingVoters);

        return decision;
    }

    /// <summary>Casts one voter's vote, degrading a thrown exception to an abstention rather than failing the whole decision.</summary>
    private async Task<VoterVote> CastVoteAsync(IRoutingVoter voter, VotingContext votingContext, CancellationToken cancellationToken)
    {
        try
        {
            return await voter.VoteAsync(votingContext, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[ORCHESTRATOR] Voter {Voter} threw while voting; treating as an abstention.", voter.Name);
            return VoterVote.Abstain(voter.Name);
        }
    }

    private double GetVoterWeight(string voterName) => voterName switch
    {
        VoterNames.DimBest => _options.DimBestVoterWeight,
        VoterNames.MemoryKnn => _options.MemoryKnnVoterWeight,
        VoterNames.LogReg => _options.LogRegVoterWeight,
        VoterNames.LlmRouter => _options.LlmRouterVoterWeight,
        _ => 1d,
    };

    private bool IsVoterEnabled(string voterName) => voterName switch
    {
        VoterNames.DimBest => _options.EnableDimBestVoter,
        VoterNames.MemoryKnn => _options.EnableMemoryKnnVoter,
        VoterNames.LogReg => _options.EnableLogRegVoter,
        VoterNames.LlmRouter => _options.EnableLlmRouterVoter,
        _ => true,
    };
}
