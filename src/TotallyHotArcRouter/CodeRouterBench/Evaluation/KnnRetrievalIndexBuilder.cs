using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Builds a <see cref="KnnRetrievalArtifact"/> from the CodeRouterBench OOD split (docs/router/
/// regret-evaluation-harness-plan.md N4): the offline, one-time step that embeds every OOD task so
/// <see cref="KnnRetrievalBaseline.Route"/> never needs to call an embedding client during replay — the
/// harness's "no live API calls" property (docs/router/regret-evaluation-harness-plan.md "Replay engine").
/// Mirrors <see cref="Router.Orchestrator.OodBootstrapSampleSource"/>'s "load OOD text, embed each task"
/// shape, reused here for the kNN comparison baseline's index rather than a live voter's training samples.
/// </summary>
public static class KnnRetrievalIndexBuilder
{
    /// <summary>
    /// Loads the OOD split's (task, winning-model) training examples via
    /// <see cref="LogRegTrainer.LoadOodTrainingExamples"/> and embeds each task's prompt text, producing
    /// one <see cref="KnnRetrievalEntry"/> per task.
    /// </summary>
    /// <param name="database">The synced CodeRouterBench corpus to read the OOD split from.</param>
    /// <param name="embeddingClient">
    /// Computes each OOD task's embedding; this is the only call site in this baseline's
    /// lifecycle that touches an embedding client.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A frozen index covering every OOD task with both extractable prompt text and at least one resolving model.</returns>
    /// <exception cref="InvalidOperationException">
    /// The corpus database is not synced, or the OOD split has no usable (task,
    /// label) pairs.
    /// </exception>
    public static async Task<KnnRetrievalArtifact> BuildAsync(
        BenchmarkDatabase database,
        IEmbeddingClient embeddingClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(embeddingClient);

        if (!File.Exists(database.DatabasePath))
            throw new InvalidOperationException(
                $"The CodeRouterBench corpus database was not found at '{database.DatabasePath}' - is the corpus synced?");

        var examples = LogRegTrainer.LoadOodTrainingExamples(database);
        if (examples.Count == 0)
            throw new InvalidOperationException(
                "No (task text, label) pairs could be built from the OOD split - is the corpus synced, " +
                "and does at least one model resolve at least one OOD task?");

        var entries = new List<KnnRetrievalEntry>(examples.Count);
        var embeddingDimension = 0;
        foreach (var example in examples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var embedding = await embeddingClient.EmbedAsync(text: example.Text, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            embeddingDimension = embedding.Vector.Length;
            entries.Add(
                new KnnRetrievalEntry(TaskId: example.TaskId, Embedding: embedding.Vector, Label: example.Label));
        }

        return new KnnRetrievalArtifact(
            EmbeddingDimension: embeddingDimension,
            EmbeddingModel: embeddingClient.ModelIdentity,
            Entries: entries,
            TrainedFrom:
            $"split='ood', tasks={entries.Count}, embeddingModel='{embeddingClient.ModelIdentity}', built {DateTimeOffset.UtcNow:O}");
    }
}