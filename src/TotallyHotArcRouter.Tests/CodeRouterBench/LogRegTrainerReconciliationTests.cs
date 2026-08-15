using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.PriceCatalog;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// The documented, reproducible <c>logreg</c> voter training recipe (PLAN.md Phase L): run
/// <see cref="LogRegTrainer.Train"/> against the real, synced probing split and, to ship a real model,
/// serialize the result through <see cref="TotallyHot.ArcRouter.Router.Orchestrator.LogRegModelArtifactSerializer.Serialize"/>
/// over <c>src/TotallyHotArcRouter/CodeRouterBench/Resources/logreg_voter_model.json</c>. Skips itself via
/// <see cref="Assert.SkipUnless"/> when <c>benchmark_id_results</c> has no <c>probing</c>-split rows - sync
/// the corpus first (Governance -> Benchmark Data, the <c>sync_benchmark_data</c> MCP tool, or
/// <c>--sync-benchmark-data</c>) - the same self-skip pattern <see cref="CodeRouterBenchTable10ReconciliationTests"/>
/// uses, since "data not synced" is an expected, non-broken state in CI and on most contributors' machines.
/// </summary>
[Trait("Category", "Integration")]
public class LogRegTrainerReconciliationTests
{
    private const string SkipReason =
        "benchmark_id_results has no 'probing'-split rows - sync the CodeRouterBench corpus first " +
        "(Governance -> Benchmark Data, the sync_benchmark_data MCP tool, or --sync-benchmark-data). " +
        "The corpus is synced on demand, never populated automatically by CI.";

    private static BenchmarkDatabase OpenRealDatabase() => new(Options.Create(new StorageOptions()));

    // Mirrors CodeRouterBenchTable10ReconciliationTests.ProbingSplitIsPopulated exactly - see its remarks
    // for why this deliberately never calls EnsureCreated against the real user database.
    private static bool ProbingSplitIsPopulated(BenchmarkDatabase database)
    {
        if (!File.Exists(database.DatabasePath))
        {
            return false;
        }

        try
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM benchmark_id_results WHERE split = 'probing';";
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// The exact reproduction recipe: this test IS the "documented, reproducible training step" PLAN.md
    /// Phase L requires. Run it locally (<c>dotnet test --filter Train_OnRealCorpus_ProducesAUsableArtifact</c>)
    /// against a synced corpus, then write <c>artifactJson</c> to the checked-in resource file to ship a
    /// real trained model in place of the placeholder.
    /// </summary>
    [Fact]
    public void Train_OnRealCorpus_ProducesAUsableArtifact()
    {
        var database = OpenRealDatabase();
        Assert.SkipUnless(ProbingSplitIsPopulated(database), SkipReason);

        var artifact = TotallyHot.ArcRouter.CodeRouterBench.LogRegTrainer.Train(database, "probing");

        Assert.False(artifact.IsPlaceholder);
        Assert.NotEmpty(artifact.Vocabulary);
        Assert.NotEmpty(artifact.ClassWeights);
        foreach (var weights in artifact.ClassWeights.Values)
        {
            Assert.Equal(artifact.Vocabulary.Count + 1, weights.Length);
        }

        var artifactJson = TotallyHot.ArcRouter.Router.Orchestrator.LogRegModelArtifactSerializer.Serialize(artifact);
        Assert.False(string.IsNullOrWhiteSpace(artifactJson));
    }
}
