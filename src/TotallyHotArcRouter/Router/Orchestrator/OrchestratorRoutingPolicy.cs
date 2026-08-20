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
/// <b>On the live path (PLAN.md Phase M).</b> <see cref="CompositeRoutingPolicy"/> dispatches every
/// non-utility request here by default - <see cref="Models.RoutingOptions.EnableOrchestratorPolicy"/>
/// is the kill switch back to <see cref="AgentRouterPolicy"/>'s memory-only ranking. An
/// explicitly-named, servable model never reaches this class at all: that routing decision is made
/// upstream, in <see cref="Proxy.RequestInterceptor"/>, before a policy is ever consulted - see
/// docs/router/orchestrator-live-path-plan.md §1.
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
/// <b>Exploration (docs/router/orchestrator-live-path-plan.md M1.2).</b> After the argmax, with
/// probability <see cref="RoutingOptions.ExplorationRate"/> (gated on
/// <see cref="RoutingOptions.EnableExploration"/>), the decision is replaced with a uniformly-random
/// pick from <see cref="RoutingContext.Candidates"/> instead - never from outside the eligible set, so
/// an exploratory pick can never bypass circuit-breaker or enabled-state checks. This is the same
/// epsilon-greedy mechanism <see cref="AgentAsARouter"/> used on the general path before this class
/// replaced it, lifted here so <see cref="RoutingOptions.EnableExploration"/>/
/// <see cref="RoutingOptions.ExplorationRate"/> keep doing something once
/// <see cref="CompositeRoutingPolicy"/> routes through here. The roll never fires on the all-abstain
/// fallback path - <see cref="RoutingDecision.CreateFallback"/> is already a degraded outcome and this
/// class does not compound it with a random pick. A decision produced by the roll sets
/// <see cref="RoutingDecision.IsExploratory"/> so a downstream reader (PLAN.md Phase N in particular)
/// can separate a deliberate probe from a genuine ensemble pick rather than scoring the former as if it
/// were the latter.
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
    /// <summary>Prefix for a per-voter breakdown key in <see cref="RoutingDecision.CandidateScores"/> (<c>"voter:{voterName}:{modelName}"</c>), distinguishing it from a plain candidate aggregate-score key.</summary>
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
    /// Delegates to the <see cref="RoutingSignals"/> overload of this method with no signals, which in
    /// turn calls <see cref="DecideAsync"/> with no task embedding/text. Callers who have a task embedding
    /// or text should use the <see cref="RoutingSignals"/> overload (as <see cref="Proxy.RequestInterceptor"/>
    /// does - docs/router/live-feedback-learning-plan.md Phase 2a) or call <see cref="DecideAsync"/>
    /// directly, so <see cref="MemoryKnnVoter"/> and <see cref="LogRegVoter"/> can participate instead of
    /// abstaining.
    /// </remarks>
    public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default) =>
        SelectModelAsync(context, signals: null, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Forwards <paramref name="signals"/>' <see cref="RoutingSignals.TaskEmbedding"/> and
    /// <see cref="RoutingSignals.TaskText"/> to <see cref="DecideAsync"/> in place of the hardcoded nulls
    /// the no-signals overload passes, so <see cref="MemoryKnnVoter"/> and <see cref="LogRegVoter"/> can
    /// participate in a live routing decision (docs/router/live-feedback-learning-plan.md Phase 2a).
    /// </remarks>
    public async Task<string> SelectModelAsync(RoutingContext context, RoutingSignals? signals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decision = await DecideAsync(context, signals?.TaskEmbedding, signals?.TaskText, cancellationToken).ConfigureAwait(false);
        return decision.SelectedModel;
    }

    /// <summary>
    /// Runs every enabled voter and returns the full weighted-vote decision, including the vote breakdown -
    /// the entry point tests and any future caller that has a task embedding/text should use instead of
    /// <see cref="SelectModelAsync(RoutingContext, CancellationToken)"/>.
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

        // Kept in separate dictionaries - not one dictionary shared between aggregate scores (keyed by
        // candidate.ModelName) and per-voter contributions (keyed "voter:{voterName}:{modelName}") - because
        // a real candidate model can itself be named "voter:custom" (see the regression test for that), which
        // would otherwise let a per-voter key collide with (and overwrite) that candidate's own aggregate score.
        var aggregateScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var voterBreakdown = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
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

            voterBreakdown[$"{VoterKeyPrefix}{voter.Name}:{canonicalModelName}"] = contribution;
            aggregateScores[canonicalModelName] = aggregateScores.GetValueOrDefault(canonicalModelName) + contribution;
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

        // Selecting winners straight out of aggregateScores by excluding the "voter:" prefix would
        // wrongly exclude a legitimate candidate model whose own name happens to start with "voter:",
        // and OrderByDescending alone leaves ties resolved by dictionary enumeration order (non-
        // deterministic). Restricting the search to context.Candidates and adding a deterministic
        // tie-break by model name avoids both.
        var best = context.Candidates
            .Select(candidate => (
                Model: candidate.ModelName,
                Score: aggregateScores.TryGetValue(candidate.ModelName, out var score) ? (double?)score : null))
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

        // Merge the two dictionaries for the exposed CandidateScores breakdown, with aggregate scores taking
        // priority - a per-voter breakdown key that happens to collide with a real candidate's aggregate key
        // (e.g. a candidate literally named "voter:dim_best:kimi-k2.5") is dropped rather than allowed to
        // overwrite that candidate's real aggregate score.
        var candidateScores = new Dictionary<string, double>(aggregateScores, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in voterBreakdown)
        {
            if (!candidateScores.TryAdd(key, value))
            {
                _logger.LogWarning(
                    "[ORCHESTRATOR] Per-voter breakdown key {Key} collides with a candidate's aggregate score for dimension {Dimension}; dropping the breakdown entry.",
                    key,
                    context.Dimension);
            }
        }

        // docs/router/orchestrator-live-path-plan.md M1.2: epsilon-greedy exploration, lifted from
        // AgentAsARouter (the policy this ensemble replaces on the general path per Phase M) so
        // EnableExploration/ExplorationRate keep mattering once CompositeRoutingPolicy routes through
        // here. Rolled after the argmax and restricted to context.Candidates - the same eligible set the
        // argmax itself drew from - so an exploratory pick can never bypass circuit-breaker or
        // enabled-state checks. Deliberately not reached on the all-abstain fallback path above: that is
        // already a degraded outcome and randomizing it would compound two unrelated failures.
        var isExploratory = _options.EnableExploration && Random.Shared.NextDouble() < _options.ExplorationRate;
        var selectedModel = isExploratory
            ? context.Candidates[Random.Shared.Next(context.Candidates.Count)].ModelName
            : best.Model;

        // docs/router/self-organizing-classification-plan.md Phase T1c: the propensity of the arm
        // actually chosen is closed-form under epsilon-greedy. K is guaranteed > 0 by the
        // Candidates.Count == 0 check above, so this never divides by zero. eps is folded to 0 when
        // exploration itself is disabled, so a non-exploring policy always reports certain selection.
        var eps = _options.EnableExploration ? _options.ExplorationRate : 0d;
        var k = context.Candidates.Count;
        var propensity = isExploratory ? eps / k : (1d - eps) + (eps / k);

        if (isExploratory)
        {
            _logger.LogInformation(
                "[ORCHESTRATOR] Exploring: selected {Model} at random instead of the argmax pick {ArgmaxModel} (rate {Rate}).",
                selectedModel,
                best.Model,
                _options.ExplorationRate);
        }

        // aggregateScores (not the merged candidateScores below) is the source of truth for a real
        // candidate's weighted score - an exploratory pick that no voter chose simply has no entry and
        // is correctly scored 0, same as any other un-voted-for candidate.
        var selectedScore = aggregateScores.TryGetValue(selectedModel, out var scoreForSelected) ? scoreForSelected : 0d;
        var confidence = effectiveWeight > 0 ? Math.Clamp(selectedScore / effectiveWeight, 0d, 1d) : 0d;
        var rationale = isExploratory
            ? $"Orchestrator exploration selected '{selectedModel}' at random (rate {_options.ExplorationRate:F2}); argmax pick was '{best.Model}' with weighted score {best.Score:F2} across {participatingVoters} voting voter(s)."
            : $"Orchestrator ensemble selected '{best.Model}' with weighted score {best.Score:F2} across {participatingVoters} voting voter(s).";
        var decision = new RoutingDecision(
            selectedModel,
            confidence,
            rationale,
            DateTimeOffset.UtcNow,
            candidateScores,
            isExploratory,
            propensity);

        _logger.LogInformation(
            "[ORCHESTRATOR] Selected {Model} for dimension {Dimension} with weighted score {Score} (confidence {Confidence}, {VoterCount} voters participated, exploratory {IsExploratory}, propensity {Propensity}).",
            decision.SelectedModel,
            context.Dimension,
            best.Score,
            decision.Confidence,
            participatingVoters,
            decision.IsExploratory,
            decision.Propensity);

        return decision;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates directly to <see cref="DecideAsync"/>, which already builds the full decision
    /// (including the real <see cref="RoutingDecision.IsExploratory"/> and
    /// <see cref="RoutingDecision.Propensity"/>) from <paramref name="signals"/> - no need for the
    /// interface's default wrap-a-string-in-a-decision implementation here.
    /// </remarks>
    public Task<RoutingDecision> DecideOutcomeAsync(RoutingContext context, RoutingSignals? signals, CancellationToken cancellationToken = default) =>
        DecideAsync(context, signals?.TaskEmbedding, signals?.TaskText, cancellationToken);

    /// <summary>
    /// Recovers which model a single voter picked from a decision's
    /// <see cref="RoutingDecision.CandidateScores"/> breakdown - the counterfactual
    /// docs/router/self-organizing-classification-plan.md Phase T4 prices its cost-savings estimate
    /// against ("already present as a vote in <c>RoutingDecision.CandidateScores</c>, requiring no new
    /// computation to obtain").
    /// </summary>
    /// <param name="decision">The decision whose breakdown to read.</param>
    /// <param name="voterName">The voter's <see cref="IRoutingVoter.Name"/>, e.g. <see cref="VoterNames.DimBest"/>.</param>
    /// <returns>
    /// The model that voter contributed to, or <see langword="null"/> when it abstained (contributing no
    /// breakdown entry at all) - the honest answer for "what would this voter alone have chosen", and never
    /// substituted with the ensemble's own pick.
    /// </returns>
    /// <remarks>
    /// A voter contributes at most one breakdown entry per decision, so in practice this finds either zero
    /// or one match; the argmax is a defensive tie-break rather than a real aggregation, and ties resolve by
    /// model name so the result never depends on dictionary enumeration order. Keys are parsed at the
    /// <em>first</em> separator after the prefix, since a model id may itself contain a colon while a voter
    /// name never does.
    /// </remarks>
    public static string? TryGetVoterPick(RoutingDecision decision, string voterName)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(voterName);

        string? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var (key, score) in decision.CandidateScores)
        {
            if (!key.StartsWith(VoterKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = key[VoterKeyPrefix.Length..];
            var separator = remainder.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || !remainder.AsSpan(0, separator).Equals(voterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var model = remainder[(separator + 1)..];
            if (model.Length == 0)
            {
                continue;
            }

            if (score > bestScore || (score == bestScore && string.CompareOrdinal(model, best) < 0))
            {
                bestScore = score;
                best = model;
            }
        }

        return best;
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

    /// <summary>Looks up the configured ensemble weight for a voter by name, defaulting to 1 for an unrecognized name so a custom voter still participates.</summary>
    /// <param name="voterName">The voter's <see cref="IRoutingVoter.Name"/>.</param>
    /// <returns>The voter's configured weight.</returns>
    private double GetVoterWeight(string voterName) => voterName switch
    {
        VoterNames.DimBest => _options.DimBestVoterWeight,
        VoterNames.MemoryKnn => _options.MemoryKnnVoterWeight,
        VoterNames.LogReg => _options.LogRegVoterWeight,
        VoterNames.LlmRouter => _options.LlmRouterVoterWeight,
        VoterNames.ClusterBest => _options.ClusterBestVoterWeight,
        _ => 1d,
    };

    /// <summary>Looks up whether a voter is enabled by name, defaulting to enabled for an unrecognized name so a custom voter is not silently excluded.</summary>
    /// <param name="voterName">The voter's <see cref="IRoutingVoter.Name"/>.</param>
    /// <returns><see langword="true"/> if the voter should participate; otherwise <see langword="false"/>.</returns>
    private bool IsVoterEnabled(string voterName) => voterName switch
    {
        VoterNames.DimBest => _options.EnableDimBestVoter,
        VoterNames.MemoryKnn => _options.EnableMemoryKnnVoter,
        VoterNames.LogReg => _options.EnableLogRegVoter,
        VoterNames.LlmRouter => _options.EnableLlmRouterVoter,
        VoterNames.ClusterBest => _options.EnableClusterBestVoter,
        _ => true,
    };
}
