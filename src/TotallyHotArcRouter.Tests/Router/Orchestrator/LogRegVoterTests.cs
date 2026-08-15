using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="LogRegVoter"/>'s tokenize -> TF-IDF -> score -> argmax mechanics against a small,
/// deterministic model artifact (not the checked-in placeholder, so these assertions do not depend on its
/// contents).
/// </summary>
public class LogRegVoterTests
{
    // Vocabulary: [bug, algorithm]. model-bug fires hard on "bug"; model-algo fires hard on "algorithm".
    private static readonly LogRegModelArtifact TestModel = new(
        Vocabulary: ["bug", "algorithm"],
        InverseDocumentFrequency: [1.0, 1.0],
        ClassWeights: new Dictionary<string, double[]>
        {
            ["model-bug"] = [0.0, 5.0, 0.0],
            ["model-algo"] = [0.0, 0.0, 5.0],
        },
        IsPlaceholder: true,
        TrainedFrom: "unit test fixture");

    [Fact]
    public async Task VoteAsync_NoTaskText_Abstains()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);
        var context = new VotingContext("live:bug_fixing", [new RoutingCandidate("model-bug", "openai", IsFree: false)]);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_TextDominatedByOneVocabularyTerm_PicksMatchingClass()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);
        var context = new VotingContext(
            "live:bug_fixing",
            [new RoutingCandidate("model-bug", "openai", IsFree: false), new RoutingCandidate("model-algo", "openai", IsFree: false)],
            TaskText: "There is a bug, a bug, a very bad bug.");

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-bug", vote.ModelName);
        Assert.InRange(vote.Confidence, 0d, 1d);
    }

    [Fact]
    public async Task VoteAsync_RestrictsToCurrentCandidates()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);
        // Text screams "algorithm", but model-algo is not an eligible candidate right now.
        var context = new VotingContext(
            "live:algorithm",
            [new RoutingCandidate("model-bug", "openai", IsFree: false)],
            TaskText: "algorithm algorithm algorithm complexity");

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-bug", vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_NoCandidateHasWeights_Abstains()
    {
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance, TestModel);
        var context = new VotingContext(
            "live:bug_fixing",
            [new RoutingCandidate("some-other-model", "openai", IsFree: false)],
            TaskText: "bug bug bug");

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public void EmbeddedModel_LoadsAndValidatesSuccessfully()
    {
        // Constructing with the default (embedded-resource) constructor exercises
        // LoadEmbeddedModel/LogRegModelArtifactSerializer against the real checked-in placeholder.
        var voter = new LogRegVoter(NullLogger<LogRegVoter>.Instance);

        Assert.Equal("logreg", voter.Name);
    }

    [Fact]
    public void Constructor_ArtifactHasMismatchedInverseDocumentFrequencyLength_ThrowsFormatException()
    {
        // The model-artifact constructor bypasses LogRegModelArtifactSerializer.Deserialize, so it must
        // validate structural invariants itself rather than letting a malformed artifact reach
        // ComputeTfIdf's index arithmetic and throw IndexOutOfRangeException later, mid-vote.
        var invalidModel = new LogRegModelArtifact(
            Vocabulary: ["bug", "algorithm"],
            InverseDocumentFrequency: [1.0],
            ClassWeights: new Dictionary<string, double[]> { ["model-bug"] = [0.0, 5.0, 0.0] },
            IsPlaceholder: true,
            TrainedFrom: "unit test fixture");

        Assert.Throws<FormatException>(() => new LogRegVoter(NullLogger<LogRegVoter>.Instance, invalidModel));
    }
}
