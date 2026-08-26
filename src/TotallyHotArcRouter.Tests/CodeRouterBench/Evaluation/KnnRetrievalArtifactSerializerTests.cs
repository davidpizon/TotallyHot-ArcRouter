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
        EmbeddingDimension: 2,
        EmbeddingModel: "test-embedding-model",
        Entries:
        [
            new KnnRetrievalEntry("t1", [1f, 0f], "model-a"),
            new KnnRetrievalEntry("t2", [0f, 1f], "model-b"),
        ],
        TrainedFrom: "unit test fixture");

    [Fact]
    public void Deserialize_ValidArtifact_RoundTrips()
    {
        var json = KnnRetrievalArtifactSerializer.Serialize(ValidArtifact);

        var artifact = KnnRetrievalArtifactSerializer.Deserialize(json);

        Assert.Equal(ValidArtifact.EmbeddingDimension, artifact.EmbeddingDimension);
        Assert.Equal(ValidArtifact.EmbeddingModel, artifact.EmbeddingModel);
        Assert.Equal(ValidArtifact.TrainedFrom, artifact.TrainedFrom);
        Assert.Equal(2, artifact.Entries.Count);
        Assert.Equal("model-a", artifact.Entries.Single(e => e.TaskId == "t1").Label);
        Assert.Equal([1f, 0f], artifact.Entries.Single(e => e.TaskId == "t1").Embedding);
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
        var json = """{"embeddingDimension":2,"embeddingModel":"m","entries":[{"taskId":"t1","embedding":[1.0],"label":"model-a"}],"trainedFrom":"x"}""";

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
        var json = """{"embeddingDimension":1,"embeddingModel":"m","entries":[{"taskId":"t1","embedding":[1e400],"label":"model-a"}],"trainedFrom":"x"}""";

        Assert.Throws<FormatException>(() => KnnRetrievalArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NullDocument_Throws() =>
        Assert.Throws<FormatException>(() => KnnRetrievalArtifactSerializer.Deserialize("null"));
}
