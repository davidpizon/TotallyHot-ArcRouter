using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="ClusterBestVoter"/>'s cluster-ledger vote and abstention rules
/// (docs/router/self-organizing-classification-plan.md Phase T3): no artifact, no embedding, an
/// embedding-dimension mismatch, an unclustered embedding, and an under-observed candidate cell must each
/// abstain cleanly rather than throw; a hand-built artifact and ledger must select the expected candidate
/// restricted to the current candidate set.
/// </summary>
public class ClusterBestVoterTests
{
    private static readonly ClusterModelArtifact TwoClusterArtifact = new(
        2,
        Centroids: [[1f, 0f], [0f, 1f]],
        2,
        TrainedAtUtc: DateTimeOffset.UtcNow,
        ClusterSizes: [0, 0],
        ClusterDimensionHistograms: [new Dictionary<string, int>(), new Dictionary<string, int>()],
        ClusterTopTerms: [[], []],
        TrainedFrom: "test",
        0,
        0);

    [Fact]
    public async Task VoteAsync_NoTaskEmbedding_Abstains()
    {
        var voter = CreateVoter(modelPath: WriteArtifact(TwoClusterArtifact), store: new FakeMemoryEntryStore());
        var context = new VotingContext(Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_NoArtifactOnDisk_AbstainsCleanlyWithoutThrowing()
    {
        var missingPath = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"), path4: "cluster_model.json");
        var voter = CreateVoter(modelPath: missingPath, store: new FakeMemoryEntryStore());
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            TaskEmbedding: [1f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_EmbeddingDimensionMismatch_Abstains()
    {
        var voter = CreateVoter(modelPath: WriteArtifact(TwoClusterArtifact), store: new FakeMemoryEntryStore());
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            // TwoClusterArtifact was trained at dimension 2; this embedding is 3-dimensional.
            TaskEmbedding: [1f, 0f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_EmbeddingBelowAssignmentThreshold_AbstainsAsUnclustered()
    {
        var store = new FakeMemoryEntryStore();
        store.Add(embedding: [1f, 0f], model: "model-a", 1.0, 5);
        // Equidistant from both centroids: similarity ~0.707 to each - below the tightened threshold used
        // for this voter, so the request must abstain as "unclustered" rather than being forced into one.
        var equidistant = Normalize([1f, 1f]);
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            TaskEmbedding: equidistant);

        var voterWithHighThreshold = CreateVoter(
            modelPath: WriteArtifact(TwoClusterArtifact), store: store, 0.99);

        var vote = await voterWithHighThreshold.VoteAsync(context: context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_CandidateBelowMinObservations_IsNotScored()
    {
        var store = new FakeMemoryEntryStore();
        store.Add(embedding: [1f, 0f], model: "model-a", 1.0, 1); // one observation, floor is 3
        var voter = CreateVoter(modelPath: WriteArtifact(TwoClusterArtifact), store: store, minObservations: 3);
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            TaskEmbedding: [1f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_PicksHighestMeanScoringCandidateInAssignedCluster()
    {
        var store = new FakeMemoryEntryStore();
        store.Add(embedding: [1f, 0f], model: "model-a", 0.2, 3);
        store.Add(embedding: [1f, 0f], model: "model-b", 0.9, 3);
        var voter = CreateVoter(modelPath: WriteArtifact(TwoClusterArtifact), store: store, minObservations: 3);
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates:
            [
                new RoutingCandidate(ModelName: "model-a", Provider: "openai", false),
                new RoutingCandidate(ModelName: "model-b", Provider: "openai", false)
            ],
            TaskEmbedding: [1f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-b", actual: vote.ModelName);
        Assert.InRange(actual: vote.Confidence, 0d, 1d);
    }

    [Fact]
    public async Task VoteAsync_RestrictsToCurrentCandidates()
    {
        var store = new FakeMemoryEntryStore();
        store.Add(embedding: [1f, 0f], model: "model-a", 0.2, 3);
        store.Add(embedding: [1f, 0f], model: "model-b", 0.9, 3);
        var voter = CreateVoter(modelPath: WriteArtifact(TwoClusterArtifact), store: store, minObservations: 3);
        // model-b scores higher, but is not an eligible candidate right now.
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            TaskEmbedding: [1f, 0f]);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-a", actual: vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_ArtifactWrittenAfterConstruction_IsPickedUpOnlyAfterReload()
    {
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(path1: directory, path2: "cluster_model.json");
        var store = new FakeMemoryEntryStore();
        store.Add(embedding: [1f, 0f], model: "model-a", 1.0, 3);
        var voter = CreateVoter(modelPath: path, store: store, minObservations: 3);
        var context = new VotingContext(
            Dimension: "live:bug_fixing",
            Candidates: [new RoutingCandidate(ModelName: "model-a", Provider: "openai", false)],
            TaskEmbedding: [1f, 0f]);

        // First vote: no file yet, so the voter both abstains and caches "no model".
        Assert.True((await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken))
            .IsAbstain);

        File.WriteAllText(path: path, contents: ClusterModelArtifactSerializer.Serialize(TwoClusterArtifact));

        // Without Reload(), the cached "no model" result is reused - the artifact is not picked up mid-run.
        Assert.True((await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken))
            .IsAbstain);

        voter.Reload();

        var voteAfterReload =
            await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(voteAfterReload.IsAbstain);
        Assert.Equal(expected: "model-a", actual: voteAfterReload.ModelName);

        Directory.Delete(path: directory, true);
    }

    [Fact]
    public void Name_IsClusterBest()
    {
        var voter = CreateVoter(modelPath: WriteArtifact(TwoClusterArtifact), store: new FakeMemoryEntryStore());

        Assert.Equal(expected: VoterNames.ClusterBest, actual: voter.Name);
    }

    private static ClusterBestVoter CreateVoter(
        string modelPath, IMemoryEntryStore store, double assignmentThreshold = 0.5, int minObservations = 1)
    {
        return new ClusterBestVoter(
            memoryEntryStore: store,
            embeddingClient: new StubEmbeddingClient(),
            routingOptions: Options.Create(new RoutingOptions
            { ClusterAssignmentThreshold = assignmentThreshold, ClusterBestMinObservations = minObservations }),
            storageOptions: Options.Create(new StorageOptions { ClusterModelPath = modelPath }),
            logger: NullLogger<ClusterBestVoter>.Instance);
    }

    private static string WriteArtifact(ClusterModelArtifact artifact)
    {
        var path = Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests",
            path3: Guid.NewGuid().ToString("N"), path4: "cluster_model.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path: path, contents: ClusterModelArtifactSerializer.Serialize(artifact));
        return path;
    }

    private static float[] Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        return [.. vector.Select(v => v / magnitude)];
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

        public void Add(float[] embedding, string model, double score, int count)
        {
            for (var i = 0; i < count; i++)
                _entries.Add(new MemoryEntry(Id: _nextId++, TaskEmbedding: embedding, ChosenModel: model, Score: score,
                    0.01, null, CreatedAtUtc: DateTimeOffset.UtcNow));
        }
    }
}