using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="EmbeddingLogRegModelArtifactSerializer.Deserialize"/>'s structural validation
/// (docs/router/live-feedback-learning-plan.md Phase 3's exit criterion: "artifact round-trips through
/// its serializer with validation rejecting malformed weight vectors, non-finite values, and dimension
/// disagreements") - rejection of a malformed or non-finite weight vector that would otherwise silently
/// propagate NaN into <see cref="LogRegVoter"/> scoring.
/// </summary>
public class EmbeddingLogRegModelArtifactSerializerTests
{
    private static readonly EmbeddingLogRegModelArtifact ValidModel = new(
        2,
        ClassWeights: new Dictionary<string, double[]>
        {
            ["model-a"] = [0.0, 5.0, 0.0]
        },
        TrainedFrom: "unit test fixture",
        176,
        42);

    [Fact]
    public void Deserialize_ValidArtifact_RoundTrips()
    {
        var json = EmbeddingLogRegModelArtifactSerializer.Serialize(ValidModel);

        var artifact = EmbeddingLogRegModelArtifactSerializer.Deserialize(json);

        Assert.Equal(expected: ValidModel.EmbeddingDimension, actual: artifact.EmbeddingDimension);
        Assert.Equal(expected: ValidModel.TrainedFrom, actual: artifact.TrainedFrom);
        Assert.Equal(expected: ValidModel.BootstrapTaskCount, actual: artifact.BootstrapTaskCount);
        Assert.Equal(expected: ValidModel.MemoryEntryCount, actual: artifact.MemoryEntryCount);
        Assert.Equal(expected: ValidModel.ClassWeights["model-a"], actual: artifact.ClassWeights["model-a"]);
    }

    [Fact]
    public void Deserialize_NonPositiveEmbeddingDimension_Throws()
    {
        var json =
            """{"embeddingDimension":0,"classWeights":{},"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => EmbeddingLogRegModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_WeightVectorLengthMismatch_Throws()
    {
        // embeddingDimension is 2, so a valid vector must have length 3 (bias + 2 components).
        var json =
            """{"embeddingDimension":2,"classWeights":{"model-a":[0.0,5.0]},"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => EmbeddingLogRegModelArtifactSerializer.Deserialize(json));
    }

    // System.Text.Json rejects a literal `NaN`/`Infinity` JSON token outright (JsonException, before our
    // validation ever runs) - the only way a non-finite double reaches Deserialize's Dto is a numeric
    // literal that overflows double range during parsing (e.g. "1e400"), which .NET's number parser
    // silently converts to +/-Infinity rather than throwing. That's the actual gap this guards.
    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void Deserialize_WeightOverflowsToInfinity_Throws(string overflowingLiteral)
    {
        var json =
            $$"""{"embeddingDimension":2,"classWeights":{"model-a":[0.0,{{overflowingLiteral}},0.0]},"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => EmbeddingLogRegModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_ClassWeightsEntryIsNull_ThrowsFormatExceptionRatherThanNullReferenceException()
    {
        var json =
            """{"embeddingDimension":2,"classWeights":{"model-a":null},"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => EmbeddingLogRegModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_ClassWeightsIsNull_ThrowsFormatExceptionRatherThanNullReferenceException()
    {
        var json =
            """{"embeddingDimension":2,"classWeights":null,"trainedFrom":"x","bootstrapTaskCount":0,"memoryEntryCount":0}""";

        Assert.Throws<FormatException>(() => EmbeddingLogRegModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NullDocument_Throws()
    {
        Assert.Throws<FormatException>(() => EmbeddingLogRegModelArtifactSerializer.Deserialize("null"));
    }

    [Fact]
    public void Validate_ArtifactWithNoClassWeights_DoesNotThrow()
    {
        // An empty ClassWeights map is structurally valid (LogRegVoter.VoteAsync itself abstains when no
        // candidate has weights) - only a positive dimension and well-formed per-class vectors are
        // required here.
        var artifact = new EmbeddingLogRegModelArtifact(
            4,
            ClassWeights: new Dictionary<string, double[]>(),
            TrainedFrom: "x",
            0,
            0);

        var exception = Record.Exception(() => EmbeddingLogRegModelArtifactSerializer.Validate(artifact));

        Assert.Null(exception);
    }
}