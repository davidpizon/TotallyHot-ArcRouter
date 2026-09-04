using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// A thin confirmation that <see cref="RegretHarnessRunner"/> (Phase N6) wraps the exact recipe
/// <see cref="N5ComparisonReportReconciliationTests"/> already proves by hand, over the same real synced
/// corpus - not a re-derivation of N5's own numbers. Skips itself via <see cref="Assert.SkipUnless"/> under
/// the same corpus-readiness precondition.
/// </summary>
[Trait(name: "Category", value: "Integration")]
public class RegretHarnessRunnerReconciliationTests
{
    private const string SkipReason =
        "The CodeRouterBench corpus needs at least one resolved OOD result and at least one id_test row - " +
        "sync it first (Governance -> Benchmark Data, the sync_benchmark_data MCP tool, or " +
        "--sync-benchmark-data). The corpus is synced on demand, never populated automatically by CI.";

    private static BenchmarkDatabase OpenRealDatabase()
    {
        return new BenchmarkDatabase(Options.Create(new StorageOptions()));
    }

    private static bool CorpusIsReady(BenchmarkDatabase database)
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

    [Fact]
    public async Task RunAsync_OnRealCorpus_CompletesWithBothSplitsReported()
    {
        var database = OpenRealDatabase();
        Assert.SkipUnless(condition: CorpusIsReady(database), reason: SkipReason);

        var runner = new RegretHarnessRunner(database: database, embeddingClient: new DeterministicFakeEmbeddingClient(),
            loggerFactory: NullLoggerFactory.Instance, logger: NullLogger<RegretHarnessRunner>.Instance);

        var stages = new List<RegretHarnessStage>();
        var progress = new Progress<RegretHarnessStage>(stages.Add);

        var result = await runner.RunAsync(stageProgress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: RegretHarnessRunResultKind.Completed, actual: result.Kind);
        Assert.NotNull(result.RanAtUtc);
        Assert.Equal(2, actual: result.Splits.Count);
        Assert.Contains(result.Splits, split => split.SplitName == "ID test" && split.MarkdownTable.Length > 0);
        Assert.Contains(result.Splits, split => split.SplitName == "OOD" && split.MarkdownTable.Length > 0);
        Assert.Same(expected: result, actual: runner.LastResult);
    }

    /// <summary>A cheap, deterministic stand-in for a real embedding model - see this type's remarks for why.</summary>
    private sealed class DeterministicFakeEmbeddingClient : IEmbeddingClient
    {
        public string ModelIdentity => "regret-harness-runner-reconciliation-fake";

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            var hash = text.GetHashCode(StringComparison.Ordinal);
            var vector = new float[8];
            for (var i = 0; i < vector.Length; i++) vector[i] = ((hash >> (i * 4)) & 0xF) / 15f;

            return Task.FromResult(new EmbeddingResult(Vector: vector, 0));
        }
    }
}
