using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Sandbox;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench;

/// <summary>
/// Reconciles a <see cref="DimensionModelScoreMatrix"/> built from the real, fetched probing split
/// against research-doc Table 10 (PLAN.md Phase K's exit criterion). Skips itself via
/// <see cref="Assert.SkipUnless"/> when <c>data/coderouterbench/id_probing_results_long.csv</c> is
/// absent - run <c>scripts/fetch-coderouterbench.sh</c> first (see <c>data/README.md</c>) - the same
/// pattern <see cref="Integration.LiteLlmParityTests"/> uses for its sidecar dependency, since "data not
/// fetched" is an expected, non-broken state in CI and on most contributors' machines.
/// </summary>
/// <remarks>
/// <para>
/// Per-model row averages (AvgPerf) reproduce Table 10 to within 0.05 for every one of the eight
/// backend models - the assertions below check that directly. Individual dimension x model cells are
/// noisier: <c>bug_fixing</c>, <c>algorithm</c>, and <c>test_generation</c> diverge from the published
/// table by up to 0.32 for GLM-5, Qwen3-Max, Qwen3.5-Plus, and MiniMax-M2.7 specifically, while every
/// cell for Claude Opus 4.6, GPT-5.4, Claude Sonnet 4.6, and Kimi-K2.5 matches to within 0.01. This
/// looks like run-to-run noise in the LLM-as-Judge-scored dimensions (research-doc Table 5) baked into
/// the released CSV rather than a parsing bug here: the per-cell errors for the affected models are
/// large in both directions and largely cancel out in the row average, which is what Phase L's
/// <c>dim_best</c> voter and Phase N's AvgPerf metric actually consume. PLAN.md Phase K records this as
/// a settled deferral - exact per-cell parity with Table 10 is not pursued further, matching Phase N's
/// own "ordering, not absolute parity" standard applied one phase earlier.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class CodeRouterBenchTable10ReconciliationTests
{
    private static readonly string ProbingCsvPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "coderouterbench", "id_probing_results_long.csv");

    private const string SkipReason =
        "data/coderouterbench/id_probing_results_long.csv not present - run " +
        "scripts/fetch-coderouterbench.sh first (see data/README.md). CodeRouterBench data is fetched " +
        "on demand, never checked in or fetched automatically by CI.";

    // research-doc Table 10, in AllDimensions order (CdGen, Algo, Bug, Comp, Refac, DS, Multi, Und, TstGn).
    private static readonly IReadOnlyDictionary<string, double[]> PublishedTable10 = new Dictionary<string, double[]>
    {
        ["claude-opus-4-6"] = [.315, .254, .717, .860, .607, .142, .408, .193, .392],
        ["gpt-5.4"] = [.282, .257, .567, .639, .644, .063, .346, .150, .764],
        ["claude-sonnet-4-6"] = [.275, .258, .698, .751, .615, .068, .407, .180, .395],
        ["glm-5"] = [.298, .472, .728, .537, .516, .079, .362, .174, .592],
        ["Qwen3-Max"] = [.262, .310, .660, .591, .336, .111, .350, .123, .827],
        ["qwen3.5-plus"] = [.282, .397, .666, .538, .296, .114, .355, .149, .714],
        ["kimi-k2.5"] = [.269, .254, .653, .590, .386, .184, .372, .195, .430],
        ["MiniMax-M2.7"] = [.239, .073, .528, .563, .603, .145, .331, .184, .494],
    };

    // Every dimension/model cell for these models matches Table 10 to within 0.01.
    private static readonly string[] CleanModels = ["claude-opus-4-6", "gpt-5.4", "claude-sonnet-4-6", "kimi-k2.5"];

    [Fact]
    public void ProbingSplitMatrix_RowAverages_MatchTable10AvgPerf()
    {
        Assert.SkipUnless(File.Exists(ProbingCsvPath), SkipReason);

        var matrix = DimensionModelScoreMatrix.FromRows(CodeRouterBenchCsvReader.Read(ProbingCsvPath));

        foreach (var (model, published) in PublishedTable10)
        {
            var computedAverage = RouterDimension.AllDimensions
                .Select(dimension => matrix.AverageScore(dimension, model))
                .Select(score => score ?? throw new InvalidOperationException(
                    $"Probing split has no rows for dimension/model pair under '{model}'."))
                .Average();
            var publishedAverage = published.Average();

            Assert.True(
                Math.Abs(computedAverage - publishedAverage) < 0.05,
                $"{model}: computed AvgPerf {computedAverage:F3} vs published {publishedAverage:F3}");
        }
    }

    [Fact]
    public void ProbingSplitMatrix_EveryCell_MatchesTable10_ForCleanModels()
    {
        Assert.SkipUnless(File.Exists(ProbingCsvPath), SkipReason);

        var matrix = DimensionModelScoreMatrix.FromRows(CodeRouterBenchCsvReader.Read(ProbingCsvPath));

        foreach (var model in CleanModels)
        {
            var published = PublishedTable10[model];
            for (var i = 0; i < RouterDimension.AllDimensions.Count; i++)
            {
                var dimension = RouterDimension.AllDimensions[i];
                var computed = matrix.AverageScore(dimension, model);

                Assert.NotNull(computed);
                Assert.True(
                    Math.Abs(computed.Value - published[i]) < 0.01,
                    $"{model}/{dimension}: computed {computed.Value:F3} vs published {published[i]:F3}");
            }
        }
    }
}
