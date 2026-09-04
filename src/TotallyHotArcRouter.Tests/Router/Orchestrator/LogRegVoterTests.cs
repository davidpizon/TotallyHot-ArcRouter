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
        2,
        ClassWeights: new Dictionary<string, double[]>
        {
            ["model-x"] = [0.0, 5.0, 0.0],
            ["model-y"] = [0.0, 0.0, 5.0]
        },
        TrainedFrom: "unit test fixture",
        176,
        0);

    [Fact]
    public async Task VoteAsync_NoTaskEmbedding_Abstains()
    {
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance, model: TestModel);
        var context = new VotingContext(Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-x", Provider: "openai", false)]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_EmbeddingDominatedByOneComponent_PicksMatchingClass()
    {
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance, model: TestModel);
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates:
            [
                new RoutingCandidate(ModelName: "model-x", Provider: "openai", false),
                new RoutingCandidate(ModelName: "model-y", Provider: "openai", false)
            ],
            TaskEmbedding: [1.0f, 0.0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-x", actual: vote.ModelName);
        Assert.InRange(actual: vote.Confidence, 0d, 1d);
    }

    [Fact]
    public async Task VoteAsync_RestrictsToCurrentCandidates()
    {
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance, model: TestModel);
        // The embedding favors model-y, but it is not an eligible candidate right now.
        var context = new VotingContext(
            Dimension: "live:algorithm",
            Candidates: [new RoutingCandidate(ModelName: "model-x", Provider: "openai", false)],
            TaskEmbedding: [0.0f, 1.0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-x", actual: vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_NoCandidateHasWeights_Abstains()
    {
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance, model: TestModel);
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "some-other-model", Provider: "openai", false)],
            TaskEmbedding: [1.0f, 0.0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_EmbeddingDimensionMismatch_Abstains()
    {
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance, model: TestModel);
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-x", Provider: "openai", false)],
            // TestModel was trained at dimension 2; this embedding is 3-dimensional.
            TaskEmbedding: [1.0f, 0.0f, 0.0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_NoArtifactOnDisk_AbstainsCleanlyWithoutThrowing()
    {
        var storageOptions = Options.Create(new StorageOptions
        {
            LogRegModelPath = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
                path3: Guid.NewGuid().ToString("N"), path4: "logreg_voter_model.json")
        });
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance, storageOptions: storageOptions,
            embeddingClient: new StubEmbeddingClient());
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-x", Provider: "openai", false)],
            TaskEmbedding: [1.0f, 0.0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_ArtifactWrittenAfterConstruction_IsPickedUpOnlyAfterReload()
    {
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(path1: directory, path2: "logreg_voter_model.json");
        var storageOptions = Options.Create(new StorageOptions { LogRegModelPath = path });
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance, storageOptions: storageOptions,
            embeddingClient: new StubEmbeddingClient());
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-x", Provider: "openai", false)],
            TaskEmbedding: [1.0f, 0.0f]);

        // First vote: no file yet, so the voter both abstains and caches "no model".
        Assert.True((await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken))
            .IsAbstain);

        await File.WriteAllTextAsync(path: path,
            contents: EmbeddingLogRegModelArtifactSerializer.Serialize(TestModel),
            cancellationToken: TestContext.Current.CancellationToken);

        // Without Reload(), the cached "no model" result is reused - the artifact is not picked up mid-run.
        Assert.True((await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken))
            .IsAbstain);

        voter.Reload();

        var voteAfterReload =
            await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(voteAfterReload.IsAbstain);
        Assert.Equal(expected: "model-x", actual: voteAfterReload.ModelName);

        Directory.Delete(path: directory, true);
    }

    [Fact]
    public void Constructor_ArtifactHasMismatchedWeightVectorLength_ThrowsFormatException()
    {
        // The model-artifact constructor bypasses EmbeddingLogRegModelArtifactSerializer.Deserialize, so
        // it must validate structural invariants itself rather than letting a malformed artifact reach the
        // scoring loop's index arithmetic and throw IndexOutOfRangeException later, mid-vote.
        var invalidModel = new EmbeddingLogRegModelArtifact(
            2,
            ClassWeights: new Dictionary<string, double[]> { ["model-x"] = [0.0, 5.0] }, // needs length 3
            TrainedFrom: "unit test fixture",
            0,
            0);

        Assert.Throws<FormatException>(() =>
            new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance, model: invalidModel));
    }

    [Fact]
    public void Name_IsLogReg()
    {
        var voter = new LogRegVoter(logger: NullLogger<LogRegVoter>.Instance, model: TestModel);

        Assert.Equal(expected: VoterNames.LogReg, actual: voter.Name);
    }
}