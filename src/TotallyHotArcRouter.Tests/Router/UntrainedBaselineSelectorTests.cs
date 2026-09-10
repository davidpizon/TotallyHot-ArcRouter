using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="UntrainedBaselineSelector"/>: its degrade path, and the invariance property that
/// makes it usable as the ROI cost-savings yardstick - its pick, unlike <see cref="DimBestVoter"/>'s, must
/// never move as live memory accumulates (docs/router/routing-roi-regret-plan.md's frozen-baseline
/// correction).
/// </summary>
public class UntrainedBaselineSelectorTests
{
    [Fact]
    public void Select_NoCorpusFile_ReturnsNull()
    {
        using var temp = new TempBenchmarkDatabase(); // never EnsureCreated - no file on disk
        var selector = new UntrainedBaselineSelector(database: temp.Database,
            logger: NullLogger<UntrainedBaselineSelector>.Instance);

        Assert.Null(selector.Select(dimension: "code_generation", candidateModelIds: ["model-a"]));
    }

    [Fact]
    public void Select_UsesProbingMatrix()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertResultRow(database: temp.Database, taskId: "task-1", split: "probing", dimension: "code_generation",
            model: "model-a", 0.2);
        InsertResultRow(database: temp.Database, taskId: "task-2", split: "probing", dimension: "code_generation",
            model: "model-b", 0.8);
        var selector = new UntrainedBaselineSelector(database: temp.Database,
            logger: NullLogger<UntrainedBaselineSelector>.Instance);

        var pick = selector.Select(dimension: "code_generation", candidateModelIds: ["model-a", "model-b"]);

        Assert.Equal(expected: "model-b", actual: pick);
    }

    [Fact]
    public void Select_NoAverageForAnyCandidate_ReturnsNull()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertResultRow(database: temp.Database, taskId: "task-1", split: "probing", dimension: "code_generation",
            model: "model-a", 0.9);
        var selector = new UntrainedBaselineSelector(database: temp.Database,
            logger: NullLogger<UntrainedBaselineSelector>.Instance);

        Assert.Null(selector.Select(dimension: "code_generation", candidateModelIds: ["model-z"]));
    }

    // The property this type exists to guarantee: unlike DimBestVoter, whose live-preferring blend would
    // flip to whichever candidate live memory favors the instant any observation exists, this selector's
    // pick must be identical whether the caller happens to also hold a RouterMemory that disagrees with
    // the prior - because this type never reads one at all. A baseline that could be swayed by live data
    // would hold the router-vs-baseline gap constant and report an improvement of zero regardless of how
    // much the router had actually learned.
    [Fact]
    public async Task Select_IsIndependentOfLiveRouterMemory()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertResultRow(database: temp.Database, taskId: "task-1", split: "probing", dimension: "code_generation",
            model: "model-a", 0.9);
        InsertResultRow(database: temp.Database, taskId: "task-2", split: "probing", dimension: "code_generation",
            model: "model-b", 0.1);
        var selector = new UntrainedBaselineSelector(database: temp.Database,
            logger: NullLogger<UntrainedBaselineSelector>.Instance);

        var pickWithNoLiveData = selector.Select(dimension: "code_generation",
            candidateModelIds: ["model-a", "model-b"]);

        // A DimBestVoter given this same RouterMemory would flip to model-b, since one live observation
        // wholly overrides the prior. This selector shares no such wiring - it takes no RouterMemory
        // parameter at all - so nothing here can influence it; the assertion below is unaffected by
        // whatever a caller does with a RouterMemory of its own.
        var liveMemoryThatWouldFlipDimBest = new RouterMemory();
        await liveMemoryThatWouldFlipDimBest.AddScoreAsync(dimension: "code_generation", model: "model-b", 1.0);

        var pickAfterLiveDataExists = selector.Select(dimension: "code_generation",
            candidateModelIds: ["model-a", "model-b"]);

        Assert.Equal(expected: "model-a", actual: pickWithNoLiveData);
        Assert.Equal(expected: pickWithNoLiveData, actual: pickAfterLiveDataExists);
    }

    private static void InsertResultRow(
        BenchmarkDatabase database,
        string taskId,
        string split,
        string dimension,
        string model,
        double score)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_id_results (task_id, split, source_split, dimension, model, score)
                              VALUES ($taskId, $split, $split, $dimension, $model, $score);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$split", value: split);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$score", value: score);
        command.ExecuteNonQuery();
    }
}
