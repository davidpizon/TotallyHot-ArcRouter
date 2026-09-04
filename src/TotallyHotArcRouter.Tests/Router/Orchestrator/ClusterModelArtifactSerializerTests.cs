using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="ClusterModelArtifactSerializer"/> - Phase T2's exit criterion: "the artifact
/// round-trips through its serializer with validation rejecting malformed centroids and dimension
/// disagreements" (docs/router/self-organizing-classification-plan.md Phase T2).
/// </summary>
public class ClusterModelArtifactSerializerTests
{
    private static readonly ClusterModelArtifact ValidModel = new(
        2,
        Centroids: [[1f, 0f], [0f, 1f]],
        2,
        TrainedAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, offset: TimeSpan.Zero),
        ClusterSizes: [10, 12],
        ClusterDimensionHistograms:
        [
            new Dictionary<string, int> { ["bug_fixing"] = 8, ["reasoning"] = 2 },
            new Dictionary<string, int> { ["code_generation"] = 12 }
        ],
        ClusterTopTerms: [["sql", "migration"], []],
        TrainedFrom: "unit test fixture",
        176,
        22);

    [Fact]
    public void Deserialize_ValidArtifact_RoundTrips()
    {
        var json = ClusterModelArtifactSerializer.Serialize(ValidModel);

        var artifact = ClusterModelArtifactSerializer.Deserialize(json);

        Assert.Equal(expected: ValidModel.EmbeddingDimension, actual: artifact.EmbeddingDimension);
        Assert.Equal(expected: ValidModel.ChosenK, actual: artifact.ChosenK);
        Assert.Equal(expected: ValidModel.TrainedFrom, actual: artifact.TrainedFrom);
        Assert.Equal(expected: ValidModel.BootstrapTaskCount, actual: artifact.BootstrapTaskCount);
        Assert.Equal(expected: ValidModel.MemoryEntryCount, actual: artifact.MemoryEntryCount);
        Assert.Equal(expected: ValidModel.Centroids[0], actual: artifact.Centroids[0]);
        Assert.Equal(expected: ValidModel.ClusterSizes, actual: artifact.ClusterSizes);
        Assert.Equal(8, actual: artifact.ClusterDimensionHistograms[0]["bug_fixing"]);
        Assert.Equal(expected: ["sql", "migration"], actual: artifact.ClusterTopTerms[0]);
    }

    [Fact]
    public void DescribeCluster_WithTopTermsAndDominantDimension_CombinesBoth()
    {
        Assert.Equal(expected: "mostly bug_fixing: sql, migration", actual: ValidModel.DescribeCluster(0));
    }

    [Fact]
    public void DescribeCluster_NoTopTerms_FallsBackToDominantDimensionAlone()
    {
        Assert.Equal(expected: "mostly code_generation", actual: ValidModel.DescribeCluster(1));
    }

    [Fact]
    public void DescribeCluster_NoHistogramOrTerms_FallsBackToBareIndex()
    {
        var artifact = ValidModel with
        {
            ClusterDimensionHistograms = [new Dictionary<string, int>(), new Dictionary<string, int>()],
            ClusterTopTerms = [[], []]
        };

        Assert.Equal(expected: "cluster 0", actual: artifact.DescribeCluster(0));
    }

    [Fact]
    public void Deserialize_NonPositiveEmbeddingDimension_Throws()
    {
        var json =
            """{"embeddingDimension":0,"centroids":[],"chosenK":0,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[],"clusterDimensionHistograms":[],"clusterTopTerms":[],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NoCentroids_Throws()
    {
        var json =
            """{"embeddingDimension":2,"centroids":[],"chosenK":0,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[],"clusterDimensionHistograms":[],"clusterTopTerms":[],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_CentroidLengthMismatch_Throws()
    {
        // embeddingDimension is 2, but the one centroid below has length 3.
        var json =
            """{"embeddingDimension":2,"centroids":[[1.0,0.0,0.0]],"chosenK":1,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[1],"clusterDimensionHistograms":[{}],"clusterTopTerms":[[]],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_ClusterSizesLengthMismatch_Throws()
    {
        var json =
            """{"embeddingDimension":2,"centroids":[[1.0,0.0],[0.0,1.0]],"chosenK":2,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[1],"clusterDimensionHistograms":[{},{}],"clusterTopTerms":[[],[]],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void Deserialize_CentroidComponentOverflowsToInfinity_Throws(string overflowingLiteral)
    {
        var json =
            $$"""{"embeddingDimension":2,"centroids":[[0.0,{{overflowingLiteral}}]],"chosenK":1,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[1],"clusterDimensionHistograms":[{}],"clusterTopTerms":[[]],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NullDocument_Throws()
    {
        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize("null"));
    }
}