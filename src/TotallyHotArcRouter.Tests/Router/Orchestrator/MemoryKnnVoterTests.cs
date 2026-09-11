using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>Covers <see cref="MemoryKnnVoter"/>'s similarity-weighted vote over <see cref="EmbeddingMemory"/> neighbors.</summary>
public class MemoryKnnVoterTests
{
    [Fact]
    public async Task VoteAsync_NoEmbedding_Abstains()
    {
        var memory = CreateMemory();
        var voter = CreateVoter(memory);
        var context = new VotingContext(Dimension: "live:code_generation",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_NoNeighbors_Abstains()
    {
        var memory = CreateMemory();
        await memory.InitializeAsync(TestContext.Current.CancellationToken);
        var voter = CreateVoter(memory);
        var context = new VotingContext(
            Dimension: "live:code_generation",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            TaskEmbedding: [1f, 0f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_PicksModelWithHighestSimilarityWeightedAverageScore()
    {
        var memory = CreateMemory();
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        // model-a: one exact-match neighbor with a poor score.
        await memory.AddEntryAsync(taskEmbedding: [1f, 0f, 0f], chosenModel: "model-a", 0.2, 0.01, null,
            cancellationToken: TestContext.Current.CancellationToken);
        // model-b: one exact-match neighbor with a great score.
        await memory.AddEntryAsync(taskEmbedding: [1f, 0f, 0f], chosenModel: "model-b", 0.9, 0.01, null,
            cancellationToken: TestContext.Current.CancellationToken);

        var voter = CreateVoter(memory);
        var context = new VotingContext(
            Dimension: "live:code_generation",
            Candidates:
            [
                new RoutingCandidate(ModelName: "model-a", Provider: "openai", false),
                new RoutingCandidate(ModelName: "model-b", Provider: "openai", false)
            ],
            TaskEmbedding: [1f, 0f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-b", actual: vote.ModelName);
        Assert.Equal(0.9, actual: vote.Confidence, 3);
    }

    [Fact]
    public async Task VoteAsync_IgnoresNeighborsForModelsNotInCurrentCandidates()
    {
        var memory = CreateMemory();
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        // model-not-a-candidate scores far higher, but is not eligible right now.
        await memory.AddEntryAsync(taskEmbedding: [1f, 0f, 0f], chosenModel: "model-not-a-candidate", 1.0, 0.01, null,
            cancellationToken: TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(taskEmbedding: [1f, 0f, 0f], chosenModel: "model-a", 0.4, 0.01, null,
            cancellationToken: TestContext.Current.CancellationToken);

        var voter = CreateVoter(memory);
        var context = new VotingContext(
            Dimension: "live:code_generation",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            TaskEmbedding: [1f, 0f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-a", actual: vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_TiedSimilarityWeightedAverages_BreaksTieDeterministicallyByModelName()
    {
        // model-b and model-a end up with identical similarity-weighted averages (same score, same exact-
        // match similarity). Plain ">" comparison over Dictionary enumeration order would leave the winner
        // unspecified; the fix must resolve this the same way every run.
        var memory = CreateMemory();
        await memory.InitializeAsync(TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(taskEmbedding: [1f, 0f, 0f], chosenModel: "model-b", 0.6, 0.01, null,
            cancellationToken: TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(taskEmbedding: [1f, 0f, 0f], chosenModel: "model-a", 0.6, 0.01, null,
            cancellationToken: TestContext.Current.CancellationToken);

        var voter = CreateVoter(memory);
        var context = new VotingContext(
            Dimension: "live:code_generation",
            Candidates:
            [
                new RoutingCandidate(ModelName: "model-a", Provider: "openai", false),
                new RoutingCandidate(ModelName: "model-b", Provider: "openai", false)
            ],
            TaskEmbedding: [1f, 0f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "model-a", actual: vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_JudgeScoredNeighbor_ExcludePolicy_IsIgnored()
    {
        var memory = CreateMemory();
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        // Only a judge-scored, high-scoring entry exists for model-a - Exclude must abstain rather than vote.
        await memory.AddEntryAsync(taskEmbedding: [1f, 0f, 0f], chosenModel: "model-a", 0.9, 0.01, null,
            cancellationToken: TestContext.Current.CancellationToken, isJudgeScored: true);

        var voter = CreateVoter(memory, new RoutingOptions { JudgeScoredRowPolicy = JudgeRowPolicy.Exclude });
        var context = new VotingContext(Dimension: "live:code_generation",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            TaskEmbedding: [1f, 0f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_DownWeightPolicy_PullsTheWeightedAverageTowardTheNonJudgeScoredNeighbor()
    {
        var memory = CreateMemory();
        await memory.InitializeAsync(TestContext.Current.CancellationToken);

        // Two exact-match neighbors for the same model: a non-judge-scored 0.9 and a judge-scored 0.3.
        await memory.AddEntryAsync(taskEmbedding: [1f, 0f, 0f], chosenModel: "model-a", 0.9, 0.01, null,
            cancellationToken: TestContext.Current.CancellationToken);
        await memory.AddEntryAsync(taskEmbedding: [1f, 0f, 0f], chosenModel: "model-a", 0.3, 0.01, null,
            cancellationToken: TestContext.Current.CancellationToken, isJudgeScored: true);

        var context = new VotingContext(Dimension: "live:code_generation",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            TaskEmbedding: [1f, 0f, 0f]);

        // Include: plain average of both neighbors, (0.9 + 0.3) / 2.
        var includeVote = await CreateVoter(memory, new RoutingOptions { JudgeScoredRowPolicy = JudgeRowPolicy.Include })
            .VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0.6, actual: includeVote.Confidence, 3);

        // DownWeight at 0.5: (0.9*1 + 0.3*0.5) / (1 + 0.5) = 1.05 / 1.5.
        var downWeightVote = await CreateVoter(memory,
                new RoutingOptions { JudgeScoredRowPolicy = JudgeRowPolicy.DownWeight, JudgeScoredRowWeight = 0.5 })
            .VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0.7, actual: downWeightVote.Confidence, 3);

        // Exclude: the judge-scored neighbor is dropped entirely, leaving just the 0.9 neighbor.
        var excludeVote = await CreateVoter(memory, new RoutingOptions { JudgeScoredRowPolicy = JudgeRowPolicy.Exclude })
            .VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0.9, actual: excludeVote.Confidence, 3);
    }

    private static MemoryKnnVoter CreateVoter(EmbeddingMemory memory, RoutingOptions? routingOptions = null)
    {
        return new MemoryKnnVoter(embeddingMemory: memory, routingOptions: Options.Create(routingOptions ?? new RoutingOptions()));
    }

    private static EmbeddingMemory CreateMemory()
    {
        return new EmbeddingMemory(store: new FakeMemoryEntryStore(),
            optionsMonitor: new StaticOptionsMonitor<RoutingOptions>(new RoutingOptions
            { EmbeddingSimilarityThreshold = 0.5 }), embeddingClient: new StubEmbeddingClient(),
            logger: NullLogger<EmbeddingMemory>.Instance);
    }

    private sealed class FakeMemoryEntryStore : IMemoryEntryStore
    {
        private readonly List<MemoryEntry> _entries = [];
        private long _nextId = 1;

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);
        }

        public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            var persisted = entry with { Id = _nextId++ };
            _entries.Add(persisted);
            return Task.FromResult(persisted);
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }
    }
}