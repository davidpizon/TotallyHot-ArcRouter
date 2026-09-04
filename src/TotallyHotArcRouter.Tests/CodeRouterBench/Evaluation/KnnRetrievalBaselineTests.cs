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
    private static KnnRetrievalArtifact BuildArtifact()
    {
        return new KnnRetrievalArtifact(
            2,
            EmbeddingModel: "test-embedding-model",
            Entries:
            [
                new KnnRetrievalEntry(TaskId: "a1", Embedding: [1f, 0f], Label: "model-a"),
                new KnnRetrievalEntry(TaskId: "a2", Embedding: [0.99f, 0.01f], Label: "model-a"),
                new KnnRetrievalEntry(TaskId: "a3", Embedding: [0.98f, 0.02f], Label: "model-a"),
                new KnnRetrievalEntry(TaskId: "b1", Embedding: [0f, 1f], Label: "model-b"),
                new KnnRetrievalEntry(TaskId: "b2", Embedding: [0.01f, 0.99f], Label: "model-b"),
                new KnnRetrievalEntry(TaskId: "b3", Embedding: [0.02f, 0.98f], Label: "model-b"),
                new KnnRetrievalEntry(TaskId: "qa", Embedding: [0.97f, 0.03f], Label: "model-a"),
                new KnnRetrievalEntry(TaskId: "qb", Embedding: [0.03f, 0.97f], Label: "model-b")
            ],
            TrainedFrom: "unit test fixture");
    }

    [Fact]
    public void Route_QueryNearModelACluster_VotesModelA()
    {
        var baseline = new KnnRetrievalBaseline(artifact: BuildArtifact(), 3);
        var context = new RegretReplayContext(TaskId: "qa", Dimension: "bug_fixing",
            CandidateModelIds: ["model-a", "model-b"]);

        Assert.Equal(expected: "model-a", actual: baseline.Route(context));
    }

    [Fact]
    public void Route_QueryNearModelBCluster_VotesModelB()
    {
        var baseline = new KnnRetrievalBaseline(artifact: BuildArtifact(), 3);
        var context = new RegretReplayContext(TaskId: "qb", Dimension: "bug_fixing",
            CandidateModelIds: ["model-a", "model-b"]);

        Assert.Equal(expected: "model-b", actual: baseline.Route(context));
    }

    [Fact]
    public void Route_QueryExcludesItselfFromItsOwnNeighborSearch()
    {
        // "a1" is itself an index entry; if leave-one-out failed, its own (zero-distance) entry would
        // dominate the vote trivially. It should still correctly vote model-a from its real neighbors.
        var baseline = new KnnRetrievalBaseline(artifact: BuildArtifact(), 3);
        var context = new RegretReplayContext(TaskId: "a1", Dimension: "bug_fixing",
            CandidateModelIds: ["model-a", "model-b"]);

        Assert.Equal(expected: "model-a", actual: baseline.Route(context));
    }

    [Fact]
    public void Route_TaskIdNotInFrozenIndex_ReturnsNull()
    {
        var baseline = new KnnRetrievalBaseline(artifact: BuildArtifact(), 3);
        var context = new RegretReplayContext(TaskId: "id-test-task", Dimension: "bug_fixing",
            CandidateModelIds: ["model-a", "model-b"]);

        Assert.Null(baseline.Route(context));
    }

    [Fact]
    public void Route_NoNeighborLabelInCandidatePool_ReturnsNull()
    {
        var baseline = new KnnRetrievalBaseline(artifact: BuildArtifact(), 3);
        var context = new RegretReplayContext(TaskId: "qa", Dimension: "bug_fixing", CandidateModelIds: ["model-c"]);

        Assert.Null(baseline.Route(context));
    }

    [Fact]
    public void Constructor_NullArtifact_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new KnnRetrievalBaseline(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveK_Throws(int k)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KnnRetrievalBaseline(artifact: BuildArtifact(), k: k));
    }

    [Fact]
    public void Name_IsKnnRetrieval()
    {
        Assert.Equal(expected: "knn_retrieval", actual: new KnnRetrievalBaseline(BuildArtifact()).Name);
    }
}