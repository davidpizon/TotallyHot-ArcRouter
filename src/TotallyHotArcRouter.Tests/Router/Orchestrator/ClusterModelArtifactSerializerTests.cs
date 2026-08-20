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
        EmbeddingDimension: 2,
        Centroids: [[1f, 0f], [0f, 1f]],
        ChosenK: 2,
        TrainedAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ClusterSizes: [10, 12],
        ClusterDimensionHistograms:
        [
            new Dictionary<string, int> { ["bug_fixing"] = 8, ["reasoning"] = 2 },
            new Dictionary<string, int> { ["code_generation"] = 12 },
        ],
        ClusterTopTerms: [["sql", "migration"], []],
        TrainedFrom: "unit test fixture",
        BootstrapTaskCount: 176,
        MemoryEntryCount: 22);

    [Fact]
    public void Deserialize_ValidArtifact_RoundTrips()
    {
        var json = ClusterModelArtifactSerializer.Serialize(ValidModel);

        var artifact = ClusterModelArtifactSerializer.Deserialize(json);

        Assert.Equal(ValidModel.EmbeddingDimension, artifact.EmbeddingDimension);
        Assert.Equal(ValidModel.ChosenK, artifact.ChosenK);
        Assert.Equal(ValidModel.TrainedFrom, artifact.TrainedFrom);
        Assert.Equal(ValidModel.BootstrapTaskCount, artifact.BootstrapTaskCount);
        Assert.Equal(ValidModel.MemoryEntryCount, artifact.MemoryEntryCount);
        Assert.Equal(ValidModel.Centroids[0], artifact.Centroids[0]);
        Assert.Equal(ValidModel.ClusterSizes, artifact.ClusterSizes);
        Assert.Equal(8, artifact.ClusterDimensionHistograms[0]["bug_fixing"]);
        Assert.Equal(["sql", "migration"], artifact.ClusterTopTerms[0]);
    }

    [Fact]
    public void DescribeCluster_WithTopTermsAndDominantDimension_CombinesBoth()
    {
        Assert.Equal("mostly bug_fixing: sql, migration", ValidModel.DescribeCluster(0));
    }

    [Fact]
    public void DescribeCluster_NoTopTerms_FallsBackToDominantDimensionAlone()
    {
        Assert.Equal("mostly code_generation", ValidModel.DescribeCluster(1));
    }

    [Fact]
    public void DescribeCluster_NoHistogramOrTerms_FallsBackToBareIndex()
    {
        var artifact = ValidModel with
        {
            ClusterDimensionHistograms = [new Dictionary<string, int>(), new Dictionary<string, int>()],
            ClusterTopTerms = [[], []],
        };

        Assert.Equal("cluster 0", artifact.DescribeCluster(0));
    }

    [Fact]
    public void Deserialize_NonPositiveEmbeddingDimension_Throws()
    {
        var json = """{"embeddingDimension":0,"centroids":[],"chosenK":0,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[],"clusterDimensionHistograms":[],"clusterTopTerms":[],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NoCentroids_Throws()
    {
        var json = """{"embeddingDimension":2,"centroids":[],"chosenK":0,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[],"clusterDimensionHistograms":[],"clusterTopTerms":[],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_CentroidLengthMismatch_Throws()
    {
        // embeddingDimension is 2, but the one centroid below has length 3.
        var json = """{"embeddingDimension":2,"centroids":[[1.0,0.0,0.0]],"chosenK":1,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[1],"clusterDimensionHistograms":[{}],"clusterTopTerms":[[]],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_ClusterSizesLengthMismatch_Throws()
    {
        var json = """{"embeddingDimension":2,"centroids":[[1.0,0.0],[0.0,1.0]],"chosenK":2,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[1],"clusterDimensionHistograms":[{},{}],"clusterTopTerms":[[],[]],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void Deserialize_CentroidComponentOverflowsToInfinity_Throws(string overflowingLiteral)
    {
        var json = $$"""{"embeddingDimension":2,"centroids":[[0.0,{{overflowingLiteral}}]],"chosenK":1,"trainedAtUtc":"2026-01-01T00:00:00Z","clusterSizes":[1],"clusterDimensionHistograms":[{}],"clusterTopTerms":[[]],"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NullDocument_Throws()
    {
        Assert.Throws<FormatException>(() => ClusterModelArtifactSerializer.Deserialize("null"));
    }
}
