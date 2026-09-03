using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// Builds an isolated, offline-safe <see cref="OrchestratorArmBaseline"/> for N5
/// (docs/router/regret-evaluation-harness-plan.md "The Orchestrator arm") - the real
/// <see cref="OrchestratorRoutingPolicy"/>, wired with only the voters that have honest data to score
/// from in a one-shot offline run over a corpus with no live traffic behind it.
/// </summary>
/// <remarks>
/// <b>Only <c>dim_best</c> and <c>logreg</c> participate.</b> <c>dim_best</c> needs only the frozen
/// probing-split prior, already built for N2's <see cref="DimensionBestBaseline"/>, backed by a fresh,
/// empty <see cref="RouterMemory"/> so it never touches (or is influenced by) an operator's real live
/// memory database - it degrades to the frozen prior on every task, exactly like the baseline it is
/// compared against. <c>logreg</c> is trained fresh, in-memory, from the same OOD outcome rows and
/// precomputed embeddings N4 already computes for <see cref="KnnRetrievalBaseline"/> - via the real,
/// production <see cref="EmbeddingLogRegTrainer"/> - and never touches the operator's own
/// <c>logreg_voter_model.json</c> on disk.
/// <b>
/// <c>memory_kNN</c>, <c>cluster_best</c>, and <c>llm_router</c>
/// are deliberately excluded
/// </b>
/// , per this doc's status notes: <c>memory_kNN</c> and <c>cluster_best</c>
/// would otherwise need "memory"/cluster state manufactured from the same 176-task evaluation corpus
/// (<c>cluster_best</c> doubly so, since a taxonomy fit to 176 tasks split across ~9 dimensions would
/// leave nearly every cluster below <see cref="RoutingOptions.ClusterBestMinObservations"/> and abstain
/// everywhere anyway), and <c>llm_router</c> requires a real local-model generation call the harness's
/// "no live API calls" property forbids. This is a documented limitation of an isolated, offline harness -
/// not a claim that the live 5-voter ensemble behaves identically.
/// </remarks>
public static class OrchestratorArmFactory
{
    /// <summary>
    /// Builds the harness's Orchestrator arm from the OOD split's real outcomes and precomputed embeddings.
    /// The returned baseline is replayed against every split (docs/router/regret-evaluation-harness-plan.md's
    /// "against VotingContexts built from each task's available signals") - on OOD, both voters can fire;
    /// on ID test/probing (no text, no precomputed embedding, per <see cref="OodRegretTaskOutcomeLoader"/>'s
    /// remarks), <c>logreg</c> abstains for lack of <see cref="VotingContext.TaskEmbedding"/>
    /// and the arm reduces to <c>dim_best</c> alone - the same degrade path the live ensemble exhibits on
    /// text-free traffic.
    /// </summary>
    /// <param name="database">The synced CodeRouterBench corpus - <c>dim_best</c>'s frozen probing-split prior.</param>
    /// <param name="oodOutcomes">The OOD split's outcomes, e.g. from <see cref="OodRegretTaskOutcomeLoader.Load"/>.</param>
    /// <param name="embeddingIndex">
    /// The OOD split's precomputed embedding index, e.g. from
    /// <see cref="KnnRetrievalIndexBuilder.BuildAsync"/>.
    /// </param>
    /// <param name="loggerFactory">Creates each voter's and the policy's own logger.</param>
    /// <returns>An <see cref="OrchestratorArmBaseline"/> ready to replay against any split.</returns>
    public static OrchestratorArmBaseline Build(
        BenchmarkDatabase database,
        IReadOnlyList<RegretTaskOutcome> oodOutcomes,
        KnnRetrievalArtifact embeddingIndex,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(oodOutcomes);
        ArgumentNullException.ThrowIfNull(embeddingIndex);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var embeddingsByTaskId = embeddingIndex.Entries.ToDictionary(
            keySelector: entry => entry.TaskId,
            elementSelector: entry => entry.Embedding as float[] ?? [.. entry.Embedding],
            comparer: StringComparer.Ordinal);

        var dimBestVoter = new DimBestVoter(
            database: database,
            routerMemory: new RouterMemory(),
            logger: loggerFactory.CreateLogger<DimBestVoter>(),
            qualityOptions: Options.Create(new QualityOptions()));

        var voters = new List<IRoutingVoter> { dimBestVoter };

        var samples = BuildLogRegTrainingSamples(oodOutcomes: oodOutcomes, embeddingsByTaskId: embeddingsByTaskId);
        if (samples.Count > 0)
        {
            var artifact = EmbeddingLogRegTrainer.Train(
                samples: samples,
                embeddingDimension: embeddingIndex.EmbeddingDimension,
                trainedFrom:
                $"N5 harness bootstrap from {oodOutcomes.Count} OOD task(s), built {DateTimeOffset.UtcNow:O}",
                bootstrapTaskCount: oodOutcomes.Count,
                0,
                embeddingModel: embeddingIndex.EmbeddingModel);

            voters.Add(new LogRegVoter(logger: loggerFactory.CreateLogger<LogRegVoter>(), model: artifact));
        }

        var routingOptions = new RoutingOptions { EnableExploration = false, ExplorationRate = 0d };
        var policy = new OrchestratorRoutingPolicy(
            voters: voters,
            optionsMonitor: new FrozenOptionsMonitor<RoutingOptions>(routingOptions),
            logger: loggerFactory.CreateLogger<OrchestratorRoutingPolicy>());

        return new OrchestratorArmBaseline(policy: policy, embeddingsByTaskId: embeddingsByTaskId);
    }

    /// <summary>
    /// Joins each OOD outcome's per-model cells with that task's precomputed embedding into one
    /// <see cref="LogRegTrainingSample"/> per (task, model) pair - the same one-sample-per-row shape
    /// <see cref="Router.Orchestrator.OodBootstrapSampleSource"/> produces for live cold-start, built here
    /// from data N4 already loaded rather than a second embedding pass.
    /// </summary>
    /// <param name="oodOutcomes">The OOD split's outcomes.</param>
    /// <param name="embeddingsByTaskId">Precomputed embeddings keyed by task id.</param>
    private static List<LogRegTrainingSample> BuildLogRegTrainingSamples(
        IReadOnlyList<RegretTaskOutcome> oodOutcomes,
        IReadOnlyDictionary<string, float[]> embeddingsByTaskId)
    {
        var samples = new List<LogRegTrainingSample>();
        foreach (var outcome in oodOutcomes)
        {
            if (!embeddingsByTaskId.TryGetValue(key: outcome.TaskId, value: out var embedding)) continue;

            foreach (var (model, cell) in outcome.Cells)
                samples.Add(new LogRegTrainingSample(Embedding: embedding, ModelKey: model, Score: cell.Score, 1.0));
        }

        return samples;
    }
}