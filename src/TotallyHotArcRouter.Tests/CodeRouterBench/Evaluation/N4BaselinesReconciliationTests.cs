using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// N4's exit recipe (docs/router/regret-evaluation-harness-plan.md): replay the <c>LogReg</c> and
/// <c>kNN Retrieval</c> baselines against the real, synced OOD split and assert each produces a real,
/// non-placeholder score - not the fixture-only proof <see cref="LogRegBaselineTests"/>/
/// <see cref="KnnRetrievalBaselineTests"/> give. Their "not computable on id_test" half of the exit
/// criterion needs no real corpus and is already covered there
/// (<c>Route_TaskTextAbsent_ReturnsNull</c>/<c>Route_TaskIdNotInFrozenIndex_ReturnsNull</c>).
/// Skips itself via <see cref="Assert.SkipUnless"/> when <c>benchmark_ood_results</c> has no
/// <c>resolved = 1</c> rows, the same self-skip pattern <see cref="LogRegTrainerReconciliationTests"/> uses.
/// </summary>
/// <remarks>
/// <b>Why a deterministic fake embedding client, not the real ONNX one.</b>
/// <see cref="KnnRetrievalIndexBuilder.BuildAsync"/> only needs <em>an</em> <see cref="IEmbeddingClient"/> -
/// nothing about building a valid, non-placeholder index depends on which one. Loading the real BGE-large
/// ONNX model here would add a multi-hundred-MB one-time download and a slow first inference pass to a
/// test AGENTS.md caps at 5 seconds, for no additional proof: the property under test is "the loader joins
/// real corpus rows into a well-formed index and the baseline routes from it," not "the embedding model
/// downloads correctly." <see cref="Router.Orchestrator.OodBootstrapSampleSourceTests"/> makes the same
/// choice for the live voter's own OOD bootstrap path.
/// </remarks>
[Trait("Category", "Integration")]
public class N4BaselinesReconciliationTests
{
    private const string SkipReason =
        "benchmark_ood_results has no 'resolved = 1' rows - sync the CodeRouterBench corpus first " +
        "(Governance -> Benchmark Data, the sync_benchmark_data MCP tool, or --sync-benchmark-data). " +
        "The corpus is synced on demand, never populated automatically by CI.";

    private static BenchmarkDatabase OpenRealDatabase() => new(Options.Create(new StorageOptions()));

    // Mirrors LogRegTrainerReconciliationTests.AtLeastOneOodResultIsResolved exactly.
    private static bool AtLeastOneOodResultIsResolved(BenchmarkDatabase database)
    {
        if (!File.Exists(database.DatabasePath))
        {
            return false;
        }

        try
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM benchmark_ood_results WHERE resolved = 1;";
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// The exact reproduction recipe for N4's real-corpus exit bar. Run it locally
    /// (<c>dotnet test --filter Replay_OnRealOodCorpus_ProducesNonPlaceholderScoresForBothBaselines</c>)
    /// against a synced corpus to inspect real replay numbers for both baselines.
    /// </summary>
    [Fact]
    public async Task Replay_OnRealOodCorpus_ProducesNonPlaceholderScoresForBothBaselines()
    {
        var database = OpenRealDatabase();
        Assert.SkipUnless(AtLeastOneOodResultIsResolved(database), SkipReason);

        var outcomes = OodRegretTaskOutcomeLoader.Load(database);
        Assert.NotEmpty(outcomes);

        var logRegArtifact = LogRegTrainer.Train(database);
        var logRegBaseline = new LogRegBaseline(logRegArtifact);

        var knnArtifact = await KnnRetrievalIndexBuilder.BuildAsync(database, new DeterministicFakeEmbeddingClient(), TestContext.Current.CancellationToken);
        var knnBaseline = new KnnRetrievalBaseline(knnArtifact);

        var logRegResult = RegretReplayEngine.Replay(outcomes, logRegBaseline, RewardWeights.Canonical);
        var knnResult = RegretReplayEngine.Replay(outcomes, knnBaseline, RewardWeights.Canonical);

        Assert.True(logRegResult.ScoredTaskCount > 0, "LogReg baseline routed zero real OOD tasks.");
        Assert.True(double.IsFinite(logRegResult.CumulativeRegret));

        Assert.True(knnResult.ScoredTaskCount > 0, "kNN Retrieval baseline routed zero real OOD tasks.");
        Assert.True(double.IsFinite(knnResult.CumulativeRegret));
    }

    /// <summary>A cheap, deterministic stand-in for a real embedding model - see this type's remarks for why.</summary>
    private sealed class DeterministicFakeEmbeddingClient : IEmbeddingClient
    {
        public string ModelIdentity => "n4-reconciliation-fake";

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            // A cheap, order-independent hash-based vector - not semantically meaningful, but stable and
            // finite, which is all this reconciliation test needs from an embedding.
            var hash = text.GetHashCode(StringComparison.Ordinal);
            var vector = new float[8];
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = ((hash >> (i * 4)) & 0xF) / 15f;
            }

            return Task.FromResult(new EmbeddingResult(vector, TokenCount: 0));
        }
    }
}
