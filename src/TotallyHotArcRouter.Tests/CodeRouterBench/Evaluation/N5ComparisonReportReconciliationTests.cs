using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// N5's exit recipe (docs/router/regret-evaluation-harness-plan.md): build the full comparison report -
/// every baseline plus the Orchestrator arm - for both the ID-test and OOD splits against the real,
/// synced corpus, and evaluate PLAN.md's exit criterion (does the observed <c>CumReg</c> ordering match
/// the paper's, and does the Orchestrator beat DimensionBest). Skips itself via
/// <see cref="Assert.SkipUnless"/> when the corpus isn't synced with at least one resolved OOD result and
/// at least one ID-test row, the same self-skip pattern <see cref="LogRegTrainerReconciliationTests"/> and
/// <see cref="N4BaselinesReconciliationTests"/> use.
/// </summary>
/// <remarks>
/// Uses the same deterministic fake embedding client <see cref="N4BaselinesReconciliationTests"/> does, for
/// the same reason: building the OOD embedding index and the Orchestrator arm's <c>logreg</c> voter needs
/// <em>an</em> <see cref="IEmbeddingClient"/>, not specifically the real BGE-large ONNX one, and the real
/// one would add a multi-hundred-MB download to a test AGENTS.md caps at 5 seconds for no additional proof.
/// </remarks>
[Trait(name: "Category", value: "Integration")]
public class N5ComparisonReportReconciliationTests
{
    private const string SkipReason =
        "The CodeRouterBench corpus needs at least one resolved OOD result and at least one id_test row - " +
        "sync it first (Governance -> Benchmark Data, the sync_benchmark_data MCP tool, or " +
        "--sync-benchmark-data). The corpus is synced on demand, never populated automatically by CI.";

    private static BenchmarkDatabase OpenRealDatabase()
    {
        return new BenchmarkDatabase(Options.Create(new StorageOptions()));
    }

    private static bool CorpusIsReadyForN5(BenchmarkDatabase database)
    {
        if (!File.Exists(database.DatabasePath)) return false;

        try
        {
            using var connection = database.OpenConnection();
            using var oodCommand = connection.CreateCommand();
            oodCommand.CommandText = "SELECT COUNT(*) FROM benchmark_ood_results WHERE resolved = 1;";
            if (Convert.ToInt64(oodCommand.ExecuteScalar()) == 0) return false;

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT COUNT(*) FROM benchmark_id_results WHERE split = 'id_test';";
            return Convert.ToInt64(idCommand.ExecuteScalar()) > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// The exact reproduction recipe for N5's report. Run it locally
    /// (<c>dotnet test --filter Replay_OnRealCorpus_ProducesTheFullComparisonReport</c>) against a synced
    /// corpus to regenerate the numbers published in this doc's changelog.
    /// </summary>
    [Fact]
    public async Task Replay_OnRealCorpus_ProducesTheFullComparisonReport()
    {
        var database = OpenRealDatabase();
        Assert.SkipUnless(condition: CorpusIsReadyForN5(database), reason: SkipReason);

        var probingMatrix = DimensionModelScoreMatrix.FromDatabase(database: database, split: "probing");
        var probingOutcomes = IdSplitRegretTaskOutcomeLoader.Load(database: database, split: "probing");
        var idTestOutcomes = IdSplitRegretTaskOutcomeLoader.Load(database: database, split: "id_test");
        var oodOutcomes = OodRegretTaskOutcomeLoader.Load(database);

        var logRegArtifact = LogRegTrainer.Train(database);
        var knnArtifact = await KnnRetrievalIndexBuilder.BuildAsync(
            database: database, embeddingClient: new DeterministicFakeEmbeddingClient(),
            cancellationToken: TestContext.Current.CancellationToken);
        var orchestratorArm = OrchestratorArmFactory.Build(database: database, oodOutcomes: oodOutcomes,
            embeddingIndex: knnArtifact, loggerFactory: NullLoggerFactory.Instance);

        var idTestReport = RegretComparisonReportBuilder.BuildReport(
            outcomes: idTestOutcomes, probingOutcomes: probingOutcomes, probingMatrix: probingMatrix,
            logRegArtifact: logRegArtifact, knnArtifact: knnArtifact, orchestratorArm: orchestratorArm,
            weights: RewardWeights.Canonical);
        var oodReport = RegretComparisonReportBuilder.BuildReport(
            outcomes: oodOutcomes, probingOutcomes: probingOutcomes, probingMatrix: probingMatrix,
            logRegArtifact: logRegArtifact, knnArtifact: knnArtifact, orchestratorArm: orchestratorArm,
            weights: RewardWeights.Canonical);

        Assert.NotEmpty(idTestReport);
        Assert.NotEmpty(oodReport);
        Assert.All(collection: idTestReport, action: row => Assert.True(double.IsFinite(row.CumulativeRegret)));
        Assert.All(collection: oodReport, action: row => Assert.True(double.IsFinite(row.CumulativeRegret)));

        // Published for a human to read and copy into the harness plan doc's changelog - this is the
        // "publish the numbers obtained either way" recipe, not an assertion on their content.
        Console.WriteLine(RegretComparisonReportBuilder.FormatMarkdownTable(title: "ID test", rows: idTestReport));
        Console.WriteLine();
        Console.WriteLine(RegretComparisonReportBuilder.FormatMarkdownTable(title: "OOD", rows: oodReport));

        var orchestratorIdTest = idTestReport.Single(row => row.RouterName == "orchestrator");
        var dimBestIdTest = idTestReport.Single(row => row.RouterName == "dim_best");
        Console.WriteLine();
        Console.WriteLine(
            $"ID-test: orchestrator CumReg={orchestratorIdTest.CumulativeRegret:F4}, " +
            $"dim_best CumReg={dimBestIdTest.CumulativeRegret:F4}, " +
            $"orchestrator beats dim_best: {orchestratorIdTest.CumulativeRegret < dimBestIdTest.CumulativeRegret}");
    }

    /// <summary>A cheap, deterministic stand-in for a real embedding model - see this type's remarks for why.</summary>
    private sealed class DeterministicFakeEmbeddingClient : IEmbeddingClient
    {
        public string ModelIdentity => "n5-reconciliation-fake";

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            var hash = text.GetHashCode(StringComparison.Ordinal);
            var vector = new float[8];
            for (var i = 0; i < vector.Length; i++) vector[i] = ((hash >> (i * 4)) & 0xF) / 15f;

            return Task.FromResult(new EmbeddingResult(Vector: vector, 0));
        }
    }
}