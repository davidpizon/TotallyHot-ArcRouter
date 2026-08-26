using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// Builds <see cref="ClusterTrainingSample"/> rows from the CodeRouterBench OOD split
/// (docs/router/self-organizing-classification-plan.md Phase T2d): the only split that publishes task
/// text, used to cold-start <see cref="SphericalKMeansTrainer"/> before enough live traffic has
/// accumulated - the same role <see cref="OodBootstrapSampleSource"/> plays for the <c>logreg</c> voter.
/// </summary>
/// <remarks>
/// One sample per task, not per <c>(task, model)</c> result row like <see cref="OodBootstrapSampleSource"/>:
/// clustering groups task embeddings, which do not depend on which model resolved a task. The OOD split
/// carries no dimension label of its own (only the ID split's <c>benchmark_id_tasks</c> does), so every
/// bootstrap sample's <see cref="ClusterTrainingSample.Dimension"/> is <see langword="null"/> - the
/// per-cluster dimension histogram (Phase T2e) is populated almost entirely from live traffic, which is
/// the expected, honest outcome rather than a defect.
/// </remarks>
public sealed class OodClusterBootstrapSampleSource
{
    private readonly BenchmarkDatabase _database;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ILogger<OodClusterBootstrapSampleSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OodClusterBootstrapSampleSource"/> class.
    /// </summary>
    /// <param name="database">The CodeRouterBench corpus database to read the OOD split from.</param>
    /// <param name="embeddingClient">Computes each OOD task's embedding.</param>
    /// <param name="logger">The logger.</param>
    public OodClusterBootstrapSampleSource(
        BenchmarkDatabase database,
        IEmbeddingClient embeddingClient,
        ILogger<OodClusterBootstrapSampleSource> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(embeddingClient);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _embeddingClient = embeddingClient;
        _logger = logger;
    }

    /// <summary>
    /// Loads the OOD split and embeds every task with extractable prompt text, returning one bootstrap
    /// sample per task.
    /// </summary>
    /// <param name="progress">Reports the number of tasks embedded so far, or <see langword="null"/> to skip reporting.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The bootstrap samples, one per embedded task.</returns>
    /// <exception cref="InvalidOperationException">The corpus database is not synced.</exception>
    public async Task<IReadOnlyList<ClusterTrainingSample>> LoadAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_database.DatabasePath))
        {
            throw new InvalidOperationException(
                $"The CodeRouterBench corpus database was not found at '{_database.DatabasePath}' - is the corpus synced?");
        }

        Dictionary<string, string> taskPrompts;
        try
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT task_id, raw_json FROM benchmark_ood_tasks;";
            using var reader = command.ExecuteReader();

            taskPrompts = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var taskId = reader.GetString(0);
                var text = LogRegTrainer.TryExtractPrompt(reader.GetString(1));
                if (text is not null)
                {
                    taskPrompts[taskId] = text;
                }
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"Failed to read the CodeRouterBench OOD split from '{_database.DatabasePath}'.", ex);
        }

        var samples = new List<ClusterTrainingSample>(taskPrompts.Count);
        var embeddedTaskCount = 0;
        foreach (var prompt in taskPrompts.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var embedding = await _embeddingClient.EmbedAsync(prompt, cancellationToken).ConfigureAwait(false);
            samples.Add(new ClusterTrainingSample(embedding.Vector, Dimension: null, Weight: 1.0));

            embeddedTaskCount++;
            progress?.Report(embeddedTaskCount);
        }

        _logger.LogInformation(
            "OOD cluster bootstrap source produced {SampleCount} training sample(s) from {TaskCount} embedded task(s).",
            samples.Count,
            embeddedTaskCount);

        return samples;
    }
}
