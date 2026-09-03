using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="LogRegVoter"/>'s embedding dot-product scoring and abstention rules
/// (docs/router/live-feedback-learning-plan.md Phase 3): no artifact, no embedding, and a dimension
/// mismatch must each abstain cleanly rather than throw; a small deterministic artifact must select the
/// expected candidate restricted to the current candidate set.
/// </summary>
public class LogRegVoterTests
{
    // 2-dimensional "embedding": model-x fires on the first component, model-y on the second.
    private static readonly EmbeddingLogRegModelArtifact TestModel = new(
        EmbeddingDimension: 2,
        ClassWeights: new Dictionary<string, double[]>
        {
            ["model-x"] = [0.0, 5.0, 0.0],
            ["model-y"] = [0.0, 0.0, 5.0],
        },
        TrainedFrom: "unit test fixture",
        BootstrapTaskCount: 176,
        MemoryEntryCount: 0);

    [Fact]
    public async Task VoteAsync_NoTaskEmbedding_Abstains()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);
        var context = new VotingContext("live:bug_fixing", [new RoutingCandidate("model-x", "openai", IsFree: false)]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_EmbeddingDominatedByOneComponent_PicksMatchingClass()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);
        var context = new VotingContext(
            "live:bug_fixing",
            [new RoutingCandidate("model-x", "openai", IsFree: false), new RoutingCandidate("model-y", "openai", IsFree: false)],
            TaskEmbedding: [1.0f, 0.0f]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-x", vote.ModelName);
        Assert.InRange(vote.Confidence, 0d, 1d);
    }

    [Fact]
    public async Task VoteAsync_RestrictsToCurrentCandidates()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);
        // The embedding favors model-y, but it is not an eligible candidate right now.
        var context = new VotingContext(
            "live:algorithm",
            [new RoutingCandidate("model-x", "openai", IsFree: false)],
            TaskEmbedding: [0.0f, 1.0f]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-x", vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_NoCandidateHasWeights_Abstains()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);
        var context = new VotingContext(
            "live:bug_fixing",
            [new RoutingCandidate("some-other-model", "openai", IsFree: false)],
            TaskEmbedding: [1.0f, 0.0f]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_EmbeddingDimensionMismatch_Abstains()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);
        var context = new VotingContext(
            "live:bug_fixing",
            [new RoutingCandidate("model-x", "openai", IsFree: false)],
            // TestModel was trained at dimension 2; this embedding is 3-dimensional.
            TaskEmbedding: [1.0f, 0.0f, 0.0f]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_NoArtifactOnDisk_AbstainsCleanlyWithoutThrowing()
    {
        var storageOptions = Options.Create(new StorageOptions
        {
            LogRegModelPath = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "logreg_voter_model.json"),
        });
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, storageOptions, new StubEmbeddingClient());
        var context = new VotingContext(
            "live:bug_fixing",
            [new RoutingCandidate("model-x", "openai", IsFree: false)],
            TaskEmbedding: [1.0f, 0.0f]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_ArtifactWrittenAfterConstruction_IsPickedUpOnlyAfterReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "logreg_voter_model.json");
        var storageOptions = Options.Create(new StorageOptions { LogRegModelPath = path });
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, storageOptions, new StubEmbeddingClient());
        var context = new VotingContext(
            "live:bug_fixing",
            [new RoutingCandidate("model-x", "openai", IsFree: false)],
            TaskEmbedding: [1.0f, 0.0f]);

        // First vote: no file yet, so the voter both abstains and caches "no model".
        Assert.True((await voter.VoteAsync(context, TestContext.Current.CancellationToken)).IsAbstain);

        File.WriteAllText(path, EmbeddingLogRegModelArtifactSerializer.Serialize(TestModel));

        // Without Reload(), the cached "no model" result is reused - the artifact is not picked up mid-run.
        Assert.True((await voter.VoteAsync(context, TestContext.Current.CancellationToken)).IsAbstain);

        voter.Reload();

        var voteAfterReload = await voter.VoteAsync(context, TestContext.Current.CancellationToken);
        Assert.False(voteAfterReload.IsAbstain);
        Assert.Equal("model-x", voteAfterReload.ModelName);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Constructor_ArtifactHasMismatchedWeightVectorLength_ThrowsFormatException()
    {
        // The model-artifact constructor bypasses EmbeddingLogRegModelArtifactSerializer.Deserialize, so
        // it must validate structural invariants itself rather than letting a malformed artifact reach the
        // scoring loop's index arithmetic and throw IndexOutOfRangeException later, mid-vote.
        var invalidModel = new EmbeddingLogRegModelArtifact(
            EmbeddingDimension: 2,
            ClassWeights: new Dictionary<string, double[]> { ["model-x"] = [0.0, 5.0] }, // needs length 3
            TrainedFrom: "unit test fixture",
            BootstrapTaskCount: 0,
            MemoryEntryCount: 0);

        Assert.Throws<FormatException>(() => new LogRegVoter(NullLogger<LogRegVoter>.Instance, invalidModel));
    }

    [Fact]
    public void Name_IsLogReg()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);

        Assert.Equal(VoterNames.LogReg, voter.Name);
    }
}
