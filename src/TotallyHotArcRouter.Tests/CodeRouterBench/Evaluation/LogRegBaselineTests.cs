using TotallyHot.ArcRouter.CodeRouterBench;
using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="LogRegBaseline.Route"/>'s real TF-IDF inference against a small, synthetic
/// <see cref="LogRegModelArtifact"/> trained by <see cref="LogRegTrainer"/> - and the "not computable" (a
/// <see langword="null"/> route) behavior <see cref="RegretReplayContext.TaskText"/> being unpublished
/// produces, the same signal an ID-test task would give this baseline.
/// </summary>
public class LogRegBaselineTests
{
    private static LogRegModelArtifact TrainTwoClassArtifact()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();

        for (var i = 0; i < 20; i++)
        {
            InsertTask(database: temp.Database, taskId: $"bug-{i}", dimension: "bug_fixing",
                prompt: "There is a bug and an error to fix.");
            InsertResult(database: temp.Database, taskId: $"bug-{i}", model: "model-bug", true);
            InsertResult(database: temp.Database, taskId: $"bug-{i}", model: "model-algo", false);

            InsertTask(database: temp.Database, taskId: $"algo-{i}", dimension: "algorithm",
                prompt: "Optimize this algorithm for lower complexity.");
            InsertResult(database: temp.Database, taskId: $"algo-{i}", model: "model-algo", true);
            InsertResult(database: temp.Database, taskId: $"algo-{i}", model: "model-bug", false);
        }

        return LogRegTrainer.Train(database: temp.Database, 50, 200, 0.5);
    }

    [Fact]
    public void Route_TaskTextPresent_PicksTheTrainedWinningClass()
    {
        var baseline = new LogRegBaseline(TrainTwoClassArtifact());
        var context = new RegretReplayContext(TaskId: "t1", Dimension: "bug_fixing",
            CandidateModelIds: ["model-bug", "model-algo"], TaskText: "another bug causing an error");

        Assert.Equal(expected: "model-bug", actual: baseline.Route(context));
    }

    [Fact]
    public void Route_TaskTextAbsent_ReturnsNull()
    {
        var baseline = new LogRegBaseline(TrainTwoClassArtifact());
        var context = new RegretReplayContext(TaskId: "t1", Dimension: "bug_fixing",
            CandidateModelIds: ["model-bug", "model-algo"], null);

        Assert.Null(baseline.Route(context));
    }

    [Fact]
    public void Route_NoCandidateIsAKnownClass_ReturnsNull()
    {
        var baseline = new LogRegBaseline(TrainTwoClassArtifact());
        var context = new RegretReplayContext(TaskId: "t1", Dimension: "bug_fixing",
            CandidateModelIds: ["model-unseen"], TaskText: "fix the bug");

        Assert.Null(baseline.Route(context));
    }

    [Fact]
    public void Route_RestrictsToCandidatePool_IgnoringOtherTrainedClasses()
    {
        var baseline = new LogRegBaseline(TrainTwoClassArtifact());

        // The text strongly favors "model-bug", but it is not in the candidate pool for this task's
        // outcome row, so the baseline must fall back to whichever candidate it does know about.
        var context = new RegretReplayContext(TaskId: "t1", Dimension: "bug_fixing", CandidateModelIds: ["model-algo"],
            TaskText: "another bug causing an error");

        Assert.Equal(expected: "model-algo", actual: baseline.Route(context));
    }

    [Fact]
    public void Constructor_NullArtifact_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LogRegBaseline(null!));
    }

    private static void InsertTask(BenchmarkDatabase database, string taskId, string dimension, string prompt)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_ood_tasks (task_id, source_split, bench, dimension, raw_json)
                              VALUES ($taskId, 'test', 'test-bench', $dimension, $rawJson);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$dimension", value: dimension);
        command.Parameters.AddWithValue(parameterName: "$rawJson",
            value: $$"""{"task_id":"{{taskId}}","prompt":"{{prompt}}"}""");
        command.ExecuteNonQuery();
    }

    private static void InsertResult(BenchmarkDatabase database, string taskId, string model, bool resolved)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO benchmark_ood_results (task_id, source_split, bench, dimension, model, resolved, cost_usd)
                              VALUES (
                                  $taskId,
                                  'test',
                                  'test-bench',
                                  (SELECT dimension FROM benchmark_ood_tasks WHERE task_id = $taskId),
                                  $model,
                                  $resolved,
                                  0.01);
                              """;
        command.Parameters.AddWithValue(parameterName: "$taskId", value: taskId);
        command.Parameters.AddWithValue(parameterName: "$model", value: model);
        command.Parameters.AddWithValue(parameterName: "$resolved", value: resolved ? 1 : 0);
        command.ExecuteNonQuery();
    }
}