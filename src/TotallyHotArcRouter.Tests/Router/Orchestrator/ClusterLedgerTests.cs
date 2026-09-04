using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="ClusterLedger.Build"/> - the "ledger-as-view" recomputation
/// (docs/router/self-organizing-classification-plan.md Phase T2f): every entry is re-assigned to its
/// nearest <em>current</em> centroid and aggregated fresh, rather than tracked incrementally per cluster
/// identity.
/// </summary>
public class ClusterLedgerTests
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
    public void Build_EntriesNearEachCentroid_AggregatesIntoTheMatchingCluster()
    {
        MemoryEntry[] entries =
        [
            Entry(embedding: [1, 0], model: "model-a", 1.0),
            Entry(embedding: [1, 0], model: "model-a", 0.5),
            Entry(embedding: [0, 1], model: "model-b", 0.8)
        ];

        var ledger = ClusterLedger.Build(artifact: TwoClusterArtifact, entries: entries);

        Assert.Equal(0.75, actual: ledger[0]["model-a"].MeanScore, 6);
        Assert.Equal(2, actual: ledger[0]["model-a"].ObservationCount);
        Assert.Equal(0.8, actual: ledger[1]["model-b"].MeanScore, 6);
        Assert.Equal(1, actual: ledger[1]["model-b"].ObservationCount);
    }

    [Fact]
    public void Build_EveryClusterIndexIsPresentEvenWhenEmpty()
    {
        var ledger = ClusterLedger.Build(artifact: TwoClusterArtifact, entries: []);

        Assert.Equal(2, actual: ledger.Count);
        Assert.Empty(ledger[0]);
        Assert.Empty(ledger[1]);
    }

    [Fact]
    public void Build_EntryWithMismatchedEmbeddingDimension_IsExcluded()
    {
        MemoryEntry[] entries = [Entry(embedding: [1, 0, 0], model: "model-a", 1.0)];

        var ledger = ClusterLedger.Build(artifact: TwoClusterArtifact, entries: entries);

        Assert.Empty(ledger[0]);
        Assert.Empty(ledger[1]);
    }

    [Fact]
    public void Build_EntryBelowAssignmentThreshold_IsExcluded()
    {
        // A vector equidistant from both centroids has similarity ~0.707 to each - below a 0.99 threshold,
        // it should be excluded from every cluster's ledger rather than forced into one.
        var equidistant = Normalize([1, 1]);
        MemoryEntry[] entries = [Entry(embedding: equidistant, model: "model-a", 1.0)];

        var ledger = ClusterLedger.Build(artifact: TwoClusterArtifact, entries: entries, 0.99);

        Assert.Empty(ledger[0]);
        Assert.Empty(ledger[1]);
    }

    [Fact]
    public void Build_ModelNamesAreCanonicalizedBeforeAggregating()
    {
        MemoryEntry[] entries =
        [
            Entry(embedding: [1, 0], model: "Model-A", 1.0),
            Entry(embedding: [1, 0], model: "model-a", 0.0)
        ];

        var ledger = ClusterLedger.Build(artifact: TwoClusterArtifact, entries: entries);

        Assert.Single(ledger[0]);
        Assert.Equal(2, actual: ledger[0].Values.Single().ObservationCount);
    }

    private static MemoryEntry Entry(float[] embedding, string model, double score)
    {
        return new MemoryEntry(0, TaskEmbedding: embedding, ChosenModel: model, Score: score, 0.01, null,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static float[] Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        return [.. vector.Select(v => v / magnitude)];
    }
}