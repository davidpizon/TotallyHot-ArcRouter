using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// Builds <see cref="LogRegTrainingSample"/> rows from the CodeRouterBench OOD split
/// (docs/router/live-feedback-learning-plan.md Phase 4a): the only split that publishes task text (176
/// tasks, all 8 models scored on every one - full feedback), used to cold-start
/// <see cref="EmbeddingLogRegTrainer"/> before any live traffic has accumulated.
/// </summary>
/// <remarks>
/// <b>Regression target.</b> <c>benchmark_ood_results</c> carries no <c>score</c> column, only
/// <c>resolved</c> - the same constraint <see cref="CodeRouterBench.Evaluation.LogRegTrainer"/> works around.
/// Unlike that trainer's one-winner-per-task classification labeling, this source emits one sample per
/// <c>(task, model)</c> row with target <c>1.0</c> when <c>resolved = 1</c> and <c>0.0</c> otherwise -
/// every row contributes a regression example for its own model's head, matching
/// <see cref="EmbeddingLogRegTrainer"/>'s per-model-head design more directly than a single per-task
/// winning label would.
/// </remarks>
public sealed class OodBootstrapSampleSource
{
    private readonly BenchmarkDatabase _database;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ILogger<OodBootstrapSampleSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OodBootstrapSampleSource"/> class.
    /// </summary>
    /// <param name="database">The CodeRouterBench corpus database to read the OOD split from.</param>
    /// <param name="embeddingClient">Computes each OOD task's embedding.</param>
    /// <param name="logger">The logger.</param>
    public OodBootstrapSampleSource(
        BenchmarkDatabase database,
        IEmbeddingClient embeddingClient,
        ILogger<OodBootstrapSampleSource> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(embeddingClient);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _embeddingClient = embeddingClient;
        _logger = logger;
    }

    /// <summary>
    /// Loads the OOD split, embeds each task with extractable prompt text, and returns one training
    /// sample per <c>(task, model)</c> result row. Embedding 176 prompts of ~3 KB is a one-time
    /// serialized ONNX pass, far slower than a single request-path embedding - <paramref name="progress"/>
    /// reports task count embedded so far, for a caller that wants to surface it rather than sit silent.
    /// </summary>
    /// <param name="progress">Reports the number of tasks embedded so far, or <see langword="null"/> to skip reporting.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The bootstrap samples and the number of distinct tasks that contributed at least one of them.</returns>
    /// <exception cref="InvalidOperationException">The corpus database is not synced.</exception>
    public async Task<(IReadOnlyList<LogRegTrainingSample> Samples, int TaskCount)> LoadAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_database.DatabasePath))
            // Mirrors DimBestVoter/CodeRouterBenchTable10ReconciliationTests' idiom: check existence before
            // ever opening a connection, since SQLite would otherwise create an empty file as a side effect.
            throw new InvalidOperationException(
                $"The CodeRouterBench corpus database was not found at '{_database.DatabasePath}' - is the corpus synced?");

        Dictionary<string, string> taskPrompts;
        Dictionary<string, List<(string Model, bool Resolved)>> taskResults;
        try
        {
            await using var connection = _database.OpenConnection();

            taskPrompts = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var tasksCommand = connection.CreateCommand())
            {
                tasksCommand.CommandText = "SELECT task_id, raw_json FROM benchmark_ood_tasks;";
                await using var reader = tasksCommand.ExecuteReader();
                while (reader.Read())
                {
                    var taskId = reader.GetString(0);
                    var text = LogRegTrainer.TryExtractPrompt(reader.GetString(1));
                    if (text is not null) taskPrompts[taskId] = text;
                }
            }

            taskResults = new Dictionary<string, List<(string, bool)>>(StringComparer.Ordinal);
            await using (var resultsCommand = connection.CreateCommand())
            {
                resultsCommand.CommandText = "SELECT task_id, model, resolved FROM benchmark_ood_results;";
                await using var reader = resultsCommand.ExecuteReader();
                while (reader.Read())
                {
                    var taskId = reader.GetString(0);
                    var model = ModelNameCanonicalizer.Canonicalize(reader.GetString(1));
                    var resolved = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;

                    if (!taskResults.TryGetValue(key: taskId, value: out var rows))
                    {
                        rows = [];
                        taskResults[taskId] = rows;
                    }

                    rows.Add((model, resolved));
                }
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                message: $"Failed to read the CodeRouterBench OOD split from '{_database.DatabasePath}'.",
                innerException: ex);
        }

        var samples = new List<LogRegTrainingSample>();
        var embeddedTaskCount = 0;
        foreach (var (taskId, prompt) in taskPrompts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!taskResults.TryGetValue(key: taskId, value: out var results) || results.Count == 0) continue;

            var embedding = await _embeddingClient.EmbedAsync(text: prompt, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var (model, resolved) in results)
                samples.Add(new LogRegTrainingSample(Embedding: embedding.Vector, ModelKey: model,
                    Score: resolved ? 1.0 : 0.0, 1.0));

            embeddedTaskCount++;
            progress?.Report(embeddedTaskCount);
        }

        _logger.LogInformation(
            message:
            "OOD bootstrap source produced {SampleCount} training sample(s) from {TaskCount} embedded task(s).",
            samples.Count,
            embeddedTaskCount);

        return (samples, embeddedTaskCount);
    }
}