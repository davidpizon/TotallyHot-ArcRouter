using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers the embedding-model guard on the two artifact-backed voters. Both already refused a
/// differently-<em>sized</em> embedding; these cover the case no length check can see, where the artifact
/// was fitted by a different embedding model of the same dimensionality.
/// </summary>
public sealed class VoterEmbeddingModelGuardTests
{
    private static readonly IReadOnlyList<RoutingCandidate> Candidates =
    [
        new("model-a", "openai", IsFree: false),
        new("model-b", "openai", IsFree: false),
    ];

    /// <summary>
    /// A logreg artifact fitted against a different embedding model describes a coordinate space the
    /// current client no longer produces, so scoring against it is arithmetic on unrelated numbers.
    /// </summary>
    [Fact]
    public async Task LogRegVoter_ArtifactFromADifferentEmbeddingModel_Abstains()
    {
        var voter = new LogRegVoter(
            NullLogger<LogRegVoter>.Instance,
            Artifact("model-a"),
            new StubEmbeddingClient("model-b"));

        var vote = await voter.VoteAsync(Context(), TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    /// <summary>The matching case still votes - the guard rejects a mismatch, not every artifact.</summary>
    [Fact]
    public async Task LogRegVoter_ArtifactFromTheSameEmbeddingModel_Votes()
    {
        var voter = new LogRegVoter(
            NullLogger<LogRegVoter>.Instance,
            Artifact("model-a"),
            new StubEmbeddingClient("model-a"));

        var vote = await voter.VoteAsync(Context(), TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
    }

    /// <summary>
    /// An artifact trained before this provenance existed carries no identity and is trusted, on the same
    /// reasoning <see cref="MemoryEntry.MatchesEmbeddingModel"/> documents: refusing it would disable the
    /// voter on every existing installation until it happened to retrain.
    /// </summary>
    [Fact]
    public async Task LogRegVoter_ArtifactWithNoRecordedEmbeddingModel_StillVotes()
    {
        var voter = new LogRegVoter(
            NullLogger<LogRegVoter>.Instance,
            Artifact(null),
            new StubEmbeddingClient("model-b"));

        var vote = await voter.VoteAsync(Context(), TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
    }

    /// <summary>
    /// The cluster equivalent: centroids fitted in another model's space would still yield a "nearest"
    /// one for any query, with no outward sign the answer is meaningless.
    /// </summary>
    [Fact]
    public async Task ClusterBestVoter_ArtifactFromADifferentEmbeddingModel_Abstains()
    {
        var path = WriteClusterArtifact("model-a");
        var voter = new ClusterBestVoter(
            new EmptyMemoryEntryStore(),
            new StubEmbeddingClient("model-b"),
            Options.Create(new RoutingOptions { ClusterAssignmentThreshold = 0.0, ClusterBestMinObservations = 1 }),
            Options.Create(new StorageOptions { ClusterModelPath = path }),
            NullLogger<ClusterBestVoter>.Instance);

        var vote = await voter.VoteAsync(Context(), TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    private static VotingContext Context() => new(
        "live:bug_fixing",
        Candidates,
        TaskEmbedding: [1f, 0f]);

    private static EmbeddingLogRegModelArtifact Artifact(string? embeddingModel) => new(
        EmbeddingDimension: 2,
        ClassWeights: new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["model-a"] = [0.0, 1.0, 0.0],
            ["model-b"] = [0.0, 0.0, 1.0],
        },
        TrainedFrom: "test",
        BootstrapTaskCount: 0,
        MemoryEntryCount: 0,
        EmbeddingModel: embeddingModel);

    private static string WriteClusterArtifact(string embeddingModel)
    {
        var artifact = new ClusterModelArtifact(
            EmbeddingDimension: 2,
            Centroids: [[1f, 0f]],
            ChosenK: 1,
            TrainedAtUtc: DateTimeOffset.UtcNow,
            ClusterSizes: [1],
            ClusterDimensionHistograms: [new Dictionary<string, int>(StringComparer.Ordinal)],
            ClusterTopTerms: [[]],
            TrainedFrom: "test",
            BootstrapTaskCount: 0,
            MemoryEntryCount: 0,
            EmbeddingModel: embeddingModel);

        var path = Path.Combine(
            Path.GetTempPath(), "arcrouter-tests", Guid.NewGuid().ToString("N"), "cluster_model.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ClusterModelArtifactSerializer.Serialize(artifact));
        return path;
    }

    /// <summary>An <see cref="IMemoryEntryStore"/> holding nothing, for a voter whose ledger is irrelevant to the assertion.</summary>
    private sealed class EmptyMemoryEntryStore : IMemoryEntryStore
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        /// <inheritdoc />
        public Task<MemoryEntry> AppendAsync(MemoryEntry entry, CancellationToken cancellationToken = default) =>
            Task.FromResult(entry);

        /// <inheritdoc />
        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc />
        public Task<long> GetMaxIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
    }
}
