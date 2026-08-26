using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="KnnRetrievalBaseline.Route"/>'s leave-one-out nearest-neighbor majority vote over a
/// small, synthetic <see cref="KnnRetrievalArtifact"/> - and the "not computable" (a <see langword="null"/>
/// route) behavior a query task outside the frozen index produces, the same signal an ID-test task gives
/// this baseline (docs/router/regret-evaluation-harness-plan.md N4).
/// </summary>
public class KnnRetrievalBaselineTests
{
    // Two tight clusters of unit vectors near (1,0) labeled "model-a" and near (0,1) labeled "model-b",
    // plus the two query tasks under test - "qa" sits with the model-a cluster, "qb" with model-b's.
    private static KnnRetrievalArtifact BuildArtifact() => new(
        EmbeddingDimension: 2,
        EmbeddingModel: "test-embedding-model",
        Entries:
        [
            new KnnRetrievalEntry("a1", [1f, 0f], "model-a"),
            new KnnRetrievalEntry("a2", [0.99f, 0.01f], "model-a"),
            new KnnRetrievalEntry("a3", [0.98f, 0.02f], "model-a"),
            new KnnRetrievalEntry("b1", [0f, 1f], "model-b"),
            new KnnRetrievalEntry("b2", [0.01f, 0.99f], "model-b"),
            new KnnRetrievalEntry("b3", [0.02f, 0.98f], "model-b"),
            new KnnRetrievalEntry("qa", [0.97f, 0.03f], "model-a"),
            new KnnRetrievalEntry("qb", [0.03f, 0.97f], "model-b"),
        ],
        TrainedFrom: "unit test fixture");

    [Fact]
    public void Route_QueryNearModelACluster_VotesModelA()
    {
        var baseline = new KnnRetrievalBaseline(BuildArtifact(), k: 3);
        var context = new RegretReplayContext("qa", "bug_fixing", ["model-a", "model-b"]);

        Assert.Equal("model-a", baseline.Route(context));
    }

    [Fact]
    public void Route_QueryNearModelBCluster_VotesModelB()
    {
        var baseline = new KnnRetrievalBaseline(BuildArtifact(), k: 3);
        var context = new RegretReplayContext("qb", "bug_fixing", ["model-a", "model-b"]);

        Assert.Equal("model-b", baseline.Route(context));
    }

    [Fact]
    public void Route_QueryExcludesItselfFromItsOwnNeighborSearch()
    {
        // "a1" is itself an index entry; if leave-one-out failed, its own (zero-distance) entry would
        // dominate the vote trivially. It should still correctly vote model-a from its real neighbors.
        var baseline = new KnnRetrievalBaseline(BuildArtifact(), k: 3);
        var context = new RegretReplayContext("a1", "bug_fixing", ["model-a", "model-b"]);

        Assert.Equal("model-a", baseline.Route(context));
    }

    [Fact]
    public void Route_TaskIdNotInFrozenIndex_ReturnsNull()
    {
        var baseline = new KnnRetrievalBaseline(BuildArtifact(), k: 3);
        var context = new RegretReplayContext("id-test-task", "bug_fixing", ["model-a", "model-b"]);

        Assert.Null(baseline.Route(context));
    }

    [Fact]
    public void Route_NoNeighborLabelInCandidatePool_ReturnsNull()
    {
        var baseline = new KnnRetrievalBaseline(BuildArtifact(), k: 3);
        var context = new RegretReplayContext("qa", "bug_fixing", ["model-c"]);

        Assert.Null(baseline.Route(context));
    }

    [Fact]
    public void Constructor_NullArtifact_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new KnnRetrievalBaseline(null!));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveK_Throws(int k) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new KnnRetrievalBaseline(BuildArtifact(), k));

    [Fact]
    public void Name_IsKnnRetrieval() =>
        Assert.Equal("knn_retrieval", new KnnRetrievalBaseline(BuildArtifact()).Name);
}
