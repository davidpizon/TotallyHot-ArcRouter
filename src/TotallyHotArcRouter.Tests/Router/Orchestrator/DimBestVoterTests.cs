using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>Covers <see cref="DimBestVoter"/>'s blend of the probing-set prior and live <see cref="RouterMemory"/>.</summary>
public class DimBestVoterTests
{
    [Fact]
    public async Task VoteAsync_NoCorpusFile_NoLiveMemory_Abstains()
    {
        using var temp = new TempBenchmarkDatabase(); // never EnsureCreated - no file on disk
        var voter = new DimBestVoter(temp.Database, new RouterMemory(), NullLogger<DimBestVoter>.Instance, Options.Create(new QualityOptions()));
        var context = new VotingContext(
            "live:code_generation",
            [new RoutingCandidate("model-a", "openai", IsFree: false)]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_NoCorpusFile_UsesLiveMemory()
    {
        using var temp = new TempBenchmarkDatabase();
        var memory = new RouterMemory();
        await memory.AddScoreAsync("live:code_generation", "model-a", 0.3);
        await memory.AddScoreAsync("live:code_generation", "model-b", 0.9);
        var voter = new DimBestVoter(temp.Database, memory, NullLogger<DimBestVoter>.Instance, Options.Create(new QualityOptions()));
        var context = new VotingContext(
            "live:code_generation",
            [new RoutingCandidate("model-a", "openai", IsFree: false), new RoutingCandidate("model-b", "openai", IsFree: false)]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-b", vote.ModelName);
        Assert.Equal(0.9, vote.Confidence, precision: 6);
    }

    [Fact]
    public async Task VoteAsync_BenchmarkPriorOnly_UsesProbingMatrixWhenNoLiveScore()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertResultRow(temp.Database, "task-1", "probing", "code_generation", "model-a", 0.2);
        InsertResultRow(temp.Database, "task-2", "probing", "code_generation", "model-b", 0.8);

        var voter = new DimBestVoter(temp.Database, new RouterMemory(), NullLogger<DimBestVoter>.Instance, Options.Create(new QualityOptions()));
        var context = new VotingContext(
            "code_generation",
            [new RoutingCandidate("model-a", "openai", IsFree: false), new RoutingCandidate("model-b", "openai", IsFree: false)]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-b", vote.ModelName);
        Assert.Equal(0.8, vote.Confidence, precision: 6);
    }

    [Fact]
    public async Task VoteAsync_LiveScorePresent_OverridesBenchmarkPrior()
    {
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        // Benchmark prior says model-a is best...
        InsertResultRow(temp.Database, "task-1", "probing", "code_generation", "model-a", 0.9);
        InsertResultRow(temp.Database, "task-2", "probing", "code_generation", "model-b", 0.1);

        // ...but live feedback under the SAME dimension key says model-b is now better.
        var memory = new RouterMemory();
        await memory.AddScoreAsync("code_generation", "model-b", 0.95);

        var voter = new DimBestVoter(temp.Database, memory, NullLogger<DimBestVoter>.Instance, Options.Create(new QualityOptions()));
        var context = new VotingContext(
            "code_generation",
            [new RoutingCandidate("model-a", "openai", IsFree: false), new RoutingCandidate("model-b", "openai", IsFree: false)]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        // model-a has no live score, so it still uses its benchmark prior (0.9); model-b's live score (0.95)
        // beats it - live observations win over the offline prior once they exist for that pair.
        Assert.Equal("model-b", vote.ModelName);
        Assert.Equal(0.95, vote.Confidence, precision: 6);
    }

    [Fact]
    public async Task VoteAsync_LivePrefixedDimension_StillMatchesTheProbingMatrixsUnprefixedRows()
    {
        // VotingContext.Dimension arrives as the live-prefixed RouterMemory key (e.g. "live:code_generation")
        // in real routing, but DimensionModelScoreMatrix stores rows under the bare RouterDimension key. The
        // voter must strip the prefix before querying the matrix or the probing prior can never be found.
        using var temp = new TempBenchmarkDatabase();
        temp.Database.EnsureCreated();
        InsertResultRow(temp.Database, "task-1", "probing", "code_generation", "model-a", 0.2);
        InsertResultRow(temp.Database, "task-2", "probing", "code_generation", "model-b", 0.8);

        var voter = new DimBestVoter(temp.Database, new RouterMemory(), NullLogger<DimBestVoter>.Instance, Options.Create(new QualityOptions()));
        var context = new VotingContext(
            "live:code_generation",
            [new RoutingCandidate("model-a", "openai", IsFree: false), new RoutingCandidate("model-b", "openai", IsFree: false)]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-b", vote.ModelName);
        Assert.Equal(0.8, vote.Confidence, precision: 6);
    }

    private static void InsertResultRow(
        TotallyHot.ArcRouter.CodeRouterBench.BenchmarkDatabase database,
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
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$split", split);
        command.Parameters.AddWithValue("$dimension", dimension);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$score", score);
        command.ExecuteNonQuery();
    }
}
