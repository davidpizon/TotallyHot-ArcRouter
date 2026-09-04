using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="KnnRetrievalArtifactSerializer.Deserialize"/>'s structural validation - rejection of
/// a malformed or non-finite embedding, a duplicate task id, or an empty index that would otherwise let
/// <see cref="KnnRetrievalBaseline"/> misbehave rather than fail fast at construction.
/// </summary>
public class KnnRetrievalArtifactSerializerTests
{
    private static readonly KnnRetrievalArtifact ValidArtifact = new(
        2,
        EmbeddingModel: "test-embedding-model",
        Entries:
        [
            new KnnRetrievalEntry(TaskId: "t1", Embedding: [1f, 0f], Label: "model-a"),
            new KnnRetrievalEntry(TaskId: "t2", Embedding: [0f, 1f], Label: "model-b")
        ],
        TrainedFrom: "unit test fixture");

    [Fact]
    public void Deserialize_ValidArtifact_RoundTrips()
    {
        var json = KnnRetrievalArtifactSerializer.Serialize(ValidArtifact);

        var artifact = KnnRetrievalArtifactSerializer.Deserialize(json);

        Assert.Equal(expected: ValidArtifact.EmbeddingDimension, actual: artifact.EmbeddingDimension);
        Assert.Equal(expected: ValidArtifact.EmbeddingModel, actual: artifact.EmbeddingModel);
        Assert.Equal(expected: ValidArtifact.TrainedFrom, actual: artifact.TrainedFrom);
        Assert.Equal(2, actual: artifact.Entries.Count);
        Assert.Equal(expected: "model-a", actual: artifact.Entries.Single(e => e.TaskId == "t1").Label);
        Assert.Equal(expected: [1f, 0f], actual: artifact.Entries.Single(e => e.TaskId == "t1").Embedding);
    }

    [Fact]
    public void Deserialize_NonPositiveEmbeddingDimension_Throws()
    {
        var json = """{"embeddingDimension":0,"embeddingModel":"m","entries":[],"trainedFrom":"x"}""";

        Assert.Throws<FormatException>(() => KnnRetrievalArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NoEntries_Throws()
    {
        var json = """{"embeddingDimension":2,"embeddingModel":"m","entries":[],"trainedFrom":"x"}""";

        Assert.Throws<FormatException>(() => KnnRetrievalArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_EmbeddingLengthMismatch_Throws()
    {
        var json =
            """{"embeddingDimension":2,"embeddingModel":"m","entries":[{"taskId":"t1","embedding":[1.0],"label":"model-a"}],"trainedFrom":"x"}""";

        Assert.Throws<FormatException>(() => KnnRetrievalArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_DuplicateTaskId_Throws()
    {
        var json = """
                   {"embeddingDimension":1,"embeddingModel":"m","entries":[
                       {"taskId":"t1","embedding":[1.0],"label":"model-a"},
                       {"taskId":"t1","embedding":[0.5],"label":"model-b"}
                   ],"trainedFrom":"x"}
                   """;

        Assert.Throws<FormatException>(() => KnnRetrievalArtifactSerializer.Deserialize(json));
    }

    // System.Text.Json rejects a literal `NaN`/`Infinity` JSON token outright (JsonException, before our
    // validation ever runs) - the only way a non-finite float reaches Deserialize's Dto is a numeric
    // literal that overflows float range during parsing, which .NET's number parser silently converts to
    // +/-Infinity rather than throwing. That's the actual gap this guards.
    [Fact]
    public void Deserialize_EmbeddingOverflowsToInfinity_Throws()
    {
        var json =
            """{"embeddingDimension":1,"embeddingModel":"m","entries":[{"taskId":"t1","embedding":[1e400],"label":"model-a"}],"trainedFrom":"x"}""";

        Assert.Throws<FormatException>(() => KnnRetrievalArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NullDocument_Throws()
    {
        Assert.Throws<FormatException>(() => KnnRetrievalArtifactSerializer.Deserialize("null"));
    }
}