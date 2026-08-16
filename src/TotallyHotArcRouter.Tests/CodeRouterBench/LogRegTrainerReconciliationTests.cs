using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.PriceCatalog;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// The documented, reproducible <c>logreg</c> Phase N comparison-baseline training recipe: run
/// <see cref="LogRegTrainer.Train"/> against the real, synced OOD split - the only split CodeRouterBench
/// publishes task text for (see <see cref="LogRegTrainer"/>'s remarks) - and, to inspect the result,
/// serialize it through <see cref="TotallyHot.ArcRouter.Router.Orchestrator.LogRegModelArtifactSerializer.Serialize"/>.
/// Skips itself via <see cref="Assert.SkipUnless"/> when <c>benchmark_ood_results</c> has no <c>resolved = 1</c>
/// rows - sync the corpus first (Governance -> Benchmark Data, the <c>sync_benchmark_data</c> MCP tool, or
/// <c>--sync-benchmark-data</c>) - the same self-skip pattern <see cref="CodeRouterBenchTable10ReconciliationTests"/>
/// uses, since "data not synced" is an expected, non-broken state in CI and on most contributors' machines.
/// </summary>
[Trait("Category", "Integration")]
public class LogRegTrainerReconciliationTests
{
    private const string SkipReason =
        "benchmark_ood_results has no 'resolved = 1' rows - sync the CodeRouterBench corpus first " +
        "(Governance -> Benchmark Data, the sync_benchmark_data MCP tool, or --sync-benchmark-data). " +
        "The corpus is synced on demand, never populated automatically by CI.";

    private static BenchmarkDatabase OpenRealDatabase() => new(Options.Create(new StorageOptions()));

    // Mirrors CodeRouterBenchTable10ReconciliationTests.ProbingSplitIsPopulated exactly - see its remarks
    // for why this deliberately never calls EnsureCreated against the real user database. Checks the exact
    // condition LogRegTrainer.Train needs (at least one resolved OOD result), not merely that the OOD
    // tables are non-empty - an OOD sync with zero resolves would otherwise still throw instead of skip.
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
    /// The exact reproduction recipe for Phase N's static LogReg comparison baseline. Run it locally
    /// (<c>dotnet test --filter Train_OnRealCorpus_ProducesAUsableArtifact</c>) against a synced corpus to
    /// inspect a real trained artifact.
    /// </summary>
    [Fact]
    public void Train_OnRealCorpus_ProducesAUsableArtifact()
    {
        var database = OpenRealDatabase();
        Assert.SkipUnless(AtLeastOneOodResultIsResolved(database), SkipReason);

        var artifact = TotallyHot.ArcRouter.CodeRouterBench.LogRegTrainer.Train(database);

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
