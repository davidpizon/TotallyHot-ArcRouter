using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="OpenAiResponseTextParser"/>.</summary>
public class OpenAiResponseTextParserTests
{
    [Fact]
    public void TryExtractFromNonStreamingBody_ValidMessage_ReturnsTrueWithText()
    {
        const string json = """{"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"Hi there!"}}]}""";

        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json, out var text);

        Assert.True(result);
        Assert.Equal("Hi there!", text);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_EmptyChoices_ReturnsFalse()
    {
        const string json = """{"id":"chatcmpl-1","choices":[]}""";

        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MissingMessage_ReturnsFalse()
    {
        const string json = """{"choices":[{}]}""";

        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody(json, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromNonStreamingBody_MalformedJson_ReturnsFalse()
    {
        var result = OpenAiResponseTextParser.TryExtractFromNonStreamingBody("{ not json", out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_ConcatenatesDeltasInOrder()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiResponseTextParser.TryExtractFromStreamingBuffer(sse, out var text);

        Assert.True(result);
        Assert.Equal("Hi there!", text);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_NoContentDeltas_ReturnsFalse()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var result = OpenAiResponseTextParser.TryExtractFromStreamingBuffer(sse, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractFromStreamingBuffer_EmptyBuffer_ReturnsFalse()
    {
        var result = OpenAiResponseTextParser.TryExtractFromStreamingBuffer(string.Empty, out _);

        Assert.False(result);
    }
}

