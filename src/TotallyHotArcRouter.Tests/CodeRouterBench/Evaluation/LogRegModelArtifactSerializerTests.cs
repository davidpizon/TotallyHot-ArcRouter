using TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

namespace TotallyHot.ArcRouter.Tests.CodeRouterBench.Evaluation;

/// <summary>
/// Covers <see cref="LogRegModelArtifactSerializer.Deserialize"/>'s structural validation - length
/// agreement between the vocabulary, IDF vector, and class weight vectors, and rejection of malformed or
/// non-finite values that would otherwise silently propagate NaN into <see cref="LogRegBaseline"/> scoring.
/// </summary>
public class LogRegModelArtifactSerializerTests
{
    private static readonly LogRegModelArtifact ValidModel = new(
        Vocabulary: ["bug", "algorithm"],
        InverseDocumentFrequency: [1.0, 1.0],
        ClassWeights: new Dictionary<string, double[]>
        {
            ["model-a"] = [0.0, 5.0, 0.0]
        },
        true,
        TrainedFrom: "unit test fixture");

    [Fact]
    public void Deserialize_ValidArtifact_RoundTrips()
    {
        var json = LogRegModelArtifactSerializer.Serialize(ValidModel);

        var artifact = LogRegModelArtifactSerializer.Deserialize(json);

        Assert.Equal(expected: ValidModel.Vocabulary, actual: artifact.Vocabulary);
        Assert.Equal(expected: ValidModel.InverseDocumentFrequency, actual: artifact.InverseDocumentFrequency);
    }

    // System.Text.Json rejects a literal `NaN`/`Infinity` JSON token outright (JsonException, before our
    // validation ever runs) - the only way a non-finite double reaches Deserialize's Dto is a numeric
    // literal that overflows double range during parsing (e.g. "1e400"), which .NET's number parser
    // silently converts to +/-Infinity rather than throwing. That's the actual gap this guards.
    [Fact]
    public void Deserialize_InverseDocumentFrequencyOverflowsToInfinity_Throws()
    {
        var json = """
                   {"vocabulary":["bug","algorithm"],"inverseDocumentFrequency":[1.0,1e400],"classWeights":{"model-a":[0.0,5.0,0.0]},"isPlaceholder":true,"trainedFrom":"x"}
                   """;

        Assert.Throws<FormatException>(() => LogRegModelArtifactSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void Deserialize_ClassWeightsOverflowToInfinity_Throws(string overflowingLiteral)
    {
        var json = $$"""
                     {"vocabulary":["bug","algorithm"],"inverseDocumentFrequency":[1.0,1.0],"classWeights":{"model-a":[0.0,{{overflowingLiteral}},0.0]},"isPlaceholder":true,"trainedFrom":"x"}
                     """;

        Assert.Throws<FormatException>(() => LogRegModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_DuplicateVocabularyTerms_Throws()
    {
        var json = """
                   {"vocabulary":["bug","bug"],"inverseDocumentFrequency":[1.0,1.0],"classWeights":{},"isPlaceholder":true,"trainedFrom":"x"}
                   """;

        Assert.Throws<FormatException>(() => LogRegModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_InverseDocumentFrequencyLengthMismatch_Throws()
    {
        var json = """
                   {"vocabulary":["bug","algorithm"],"inverseDocumentFrequency":[1.0],"classWeights":{},"isPlaceholder":true,"trainedFrom":"x"}
                   """;

        Assert.Throws<FormatException>(() => LogRegModelArtifactSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Deserialize_VocabularyContainsNullOrBlankTerm_Throws(string vocabularyTermLiteral)
    {
        // A null/blank vocabulary term would otherwise become a Dictionary<string, int> key in
        // LogRegVoter's vocabulary index - null throws ArgumentNullException, blank silently collides with
        // every other blank entry - so it must be rejected here as a format error instead.
        var json = $$"""
                     {"vocabulary":["bug",{{vocabularyTermLiteral}}],"inverseDocumentFrequency":[1.0,1.0],"classWeights":{},"isPlaceholder":true,"trainedFrom":"x"}
                     """;

        Assert.Throws<FormatException>(() => LogRegModelArtifactSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_ClassWeightsEntryIsNull_ThrowsFormatExceptionRatherThanNullReferenceException()
    {
        var json = """
                   {"vocabulary":["bug","algorithm"],"inverseDocumentFrequency":[1.0,1.0],"classWeights":{"model-a":null},"isPlaceholder":true,"trainedFrom":"x"}
                   """;

        Assert.Throws<FormatException>(() => LogRegModelArtifactSerializer.Deserialize(json));
    }
}