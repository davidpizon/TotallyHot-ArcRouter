namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The <c>memory_kNN</c> voter (PLAN.md Phase L): retrieves <see cref="EmbeddingMemory"/>'s top neighbors
/// for the task embedding and votes for the model with the highest similarity-weighted average observed
/// score among them, restricted to models present in <see cref="VotingContext.Candidates"/> - research-doc
/// §3.3's "top-10 historical neighbors from Memory (cosine kNN)" input to the Orchestrator.
/// </summary>
public sealed class MemoryKnnVoter : IRoutingVoter
{
    private readonly EmbeddingMemory _embeddingMemory;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryKnnVoter"/> class.
    /// </summary>
    /// <param name="embeddingMemory">The task-embedding-keyed memory this voter retrieves neighbors from.</param>
    public MemoryKnnVoter(EmbeddingMemory embeddingMemory)
    {
        ArgumentNullException.ThrowIfNull(embeddingMemory);
        _embeddingMemory = embeddingMemory;
    }

    /// <inheritdoc />
    public string Name => VoterNames.MemoryKnn;

    /// <inheritdoc />
    public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TaskEmbedding is null)
        {
            return Task.FromResult(VoterVote.Abstain(Name));
        }

        var neighbors = _embeddingMemory.FindNearest(context.TaskEmbedding);
        if (neighbors.Count == 0)
        {
            return Task.FromResult(VoterVote.Abstain(Name));
        }

        var candidateNames = new HashSet<string>(
            context.Candidates.Select(candidate => candidate.ModelName),
            StringComparer.OrdinalIgnoreCase);

        var weightedSums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var weightTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, similarity) in neighbors)
        {
            if (!candidateNames.Contains(entry.ChosenModel) || similarity <= 0)
            {
                continue;
            }

            weightedSums[entry.ChosenModel] = weightedSums.GetValueOrDefault(entry.ChosenModel) + similarity * entry.Score;
            weightTotals[entry.ChosenModel] = weightTotals.GetValueOrDefault(entry.ChosenModel) + similarity;
        }

        string? bestModel = null;
        var bestAverage = double.NegativeInfinity;
        foreach (var (model, weightTotal) in weightTotals)
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
            : new VoterVote(Name, bestModel, Math.Clamp(bestAverage, 0d, 1d));
        return Task.FromResult(vote);
    }
}
