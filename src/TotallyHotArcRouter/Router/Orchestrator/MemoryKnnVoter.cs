using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The <c>memory_kNN</c> voter (PLAN.md Phase L): retrieves <see cref="EmbeddingMemory"/>'s top neighbors
/// for the task embedding and votes for the model with the highest similarity-weighted average observed
/// score among them, restricted to models present in <see cref="VotingContext.Candidates"/> - research-doc
/// §3.3's "top-10 historical neighbors from Memory (cosine kNN)" input to the Orchestrator. A neighbor's
/// similarity weight is further scaled (or the neighbor dropped) by
/// <see cref="RoutingOptions.JudgeScoredRowPolicy"/> when it is judge-scored
/// (docs/router/geval-shadow-scoring-plan.md's G3 "still owed" item).
/// </summary>
public sealed class MemoryKnnVoter : IRoutingVoter
{
    private readonly EmbeddingMemory _embeddingMemory;
    private readonly RoutingOptions _routingOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryKnnVoter"/> class.
    /// </summary>
    /// <param name="embeddingMemory">The task-embedding-keyed memory this voter retrieves neighbors from.</param>
    /// <param name="routingOptions">Supplies the judge-scored-row policy applied to retrieved neighbors.</param>
    public MemoryKnnVoter(EmbeddingMemory embeddingMemory, IOptions<RoutingOptions> routingOptions)
    {
        ArgumentNullException.ThrowIfNull(embeddingMemory);
        ArgumentNullException.ThrowIfNull(routingOptions);
        _embeddingMemory = embeddingMemory;
        _routingOptions = routingOptions.Value;
    }

    /// <inheritdoc/>
    public string Name => VoterNames.MemoryKnn;

    /// <inheritdoc/>
    public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TaskEmbedding is null) return Task.FromResult(VoterVote.Abstain(Name));

        var neighbors = _embeddingMemory.FindNearest(context.TaskEmbedding,
            judgeRowPolicy: _routingOptions.JudgeScoredRowPolicy,
            judgeRowWeight: _routingOptions.JudgeScoredRowWeight);
        if (neighbors.Count == 0) return Task.FromResult(VoterVote.Abstain(Name));

        var candidateNames = new HashSet<string>(
            collection: context.Candidates.Select(candidate => candidate.ModelName),
            comparer: StringComparer.OrdinalIgnoreCase);

        var weightedSums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var weightTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, similarity) in neighbors)
        {
            if (!candidateNames.Contains(entry.ChosenModel) || similarity <= 0) continue;

            var judgeWeight = _routingOptions.ResolveJudgeRowWeight(entry.IsJudgeScored);
            // FindNearest already filtered by policy, so judgeWeight should not be null
            if (judgeWeight is null) continue;

            var effectiveWeight = similarity * judgeWeight.Value;
            weightedSums[entry.ChosenModel] =
                weightedSums.GetValueOrDefault(entry.ChosenModel) + effectiveWeight * entry.Score;
            weightTotals[entry.ChosenModel] = weightTotals.GetValueOrDefault(entry.ChosenModel) + effectiveWeight;
        }

        // Iterate in a fixed key order so a tie between two models' weighted averages resolves
        // deterministically (by model name) rather than by Dictionary's unspecified enumeration order.
        string? bestModel = null;
        var bestAverage = double.NegativeInfinity;
        foreach (var (model, weightTotal) in weightTotals.OrderBy(keySelector: entry => entry.Key,
                     comparer: StringComparer.Ordinal))
        {
            var average = weightedSums[model] / weightTotal;
            if (average > bestAverage)
            {
                bestAverage = average;
                bestModel = model;
            }
        }

        var vote = bestModel is null
            ? VoterVote.Abstain(Name)
            : new VoterVote(VoterName: Name, ModelName: bestModel, Confidence: Math.Clamp(value: bestAverage, 0d, 1d));
        return Task.FromResult(vote);
    }
}