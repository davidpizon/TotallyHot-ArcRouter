using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality.Extraction;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers fenced-code-block extraction, language preference, and caps.</summary>
public class CodeBlockSignalExtractorTests
{
    private static CodeBlockSignalExtractor CreateExtractor(QualityOptions? options = null)
    {
        return new CodeBlockSignalExtractor(
            dimensionInferrer: new KeywordDimensionInferrer(),
            options: Options.Create(options ?? new QualityOptions()),
            logger: NullLogger<CodeBlockSignalExtractor>.Instance);
    }

    private static SignalExtractionContext Context(string responseText, string prompt = "generate code")
    {
        return new SignalExtractionContext(ResponseText: responseText, Prompt: prompt, Model: "gpt-5.4",
            CorrelationId: "corr-1", SessionId: "sess-1");
    }

    [Fact]
    public void Extract_NoCodeBlock_ReturnsNull()
    {
        var extractor = CreateExtractor();

        Assert.Null(extractor.Extract(Context("Here is some prose with no fences.")));
    }

    [Fact]
    public void Extract_WhitespaceOnlyResponse_ReturnsNull()
    {
        var extractor = CreateExtractor();

        Assert.Null(extractor.Extract(Context("   \n\t  ")));
    }

    [Fact]
    public void Extract_PythonBlock_ReturnsRequest()
    {
        var extractor = CreateExtractor();
        var response = "Sure:\n```python\nprint('hi')\n```\nDone.";

        var request = extractor.Extract(Context(response));

        Assert.NotNull(request);
        Assert.Equal(expected: CodeLanguage.Python, actual: request.Language);
        Assert.Contains(expectedSubstring: "print('hi')", actualString: request.Code,
            comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: "gpt-5.4", actual: request.Model);
        Assert.Equal(expected: "corr-1", actual: request.CorrelationId);
    }

    [Fact]
    public void Extract_PrefersRecognizedLanguageBlock()
    {
        var extractor = CreateExtractor();
        var response = "```\nplain text\n```\nthen\n```js\nconsole.log(1)\n```";

        var request = extractor.Extract(Context(response));

        Assert.NotNull(request);
        Assert.Equal(expected: CodeLanguage.JavaScript, actual: request.Language);
    }

    [Fact]
    public void Extract_RespectsMaxCodeBytes()
    {
        var extractor = CreateExtractor(new QualityOptions { MaxCodeBytes = 5 });
        var response = "```python\n0123456789\n```";

        var request = extractor.Extract(Context(response));

        Assert.NotNull(request);
        Assert.Equal(5, actual: request.Code.Length);
    }

    [Fact]
    public void Extract_MaxCodeBlocksZero_ReturnsNull()
    {
        var extractor = CreateExtractor(new QualityOptions { MaxCodeBlocks = 0 });
        var response = "```python\nprint('hi')\n```";

        Assert.Null(extractor.Extract(Context(response)));
    }

    [Fact]
    public void Extract_TildeFence_IsRecognized()
    {
        var extractor = CreateExtractor();
        var response = "~~~python\nprint('hi')\n~~~";

        var request = extractor.Extract(Context(response));

        Assert.NotNull(request);
        Assert.Equal(expected: CodeLanguage.Python, actual: request.Language);
        Assert.Contains(expectedSubstring: "print('hi')", actualString: request.Code,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_InfersDimensionFromPrompt()
    {
        var extractor = CreateExtractor();
        var response = "```python\nx = 1\n```";

        var request = extractor.Extract(Context(responseText: response, prompt: "fix the bug in this code"));

        Assert.NotNull(request);
        Assert.Equal(expected: "bug_fixing", actual: request.Dimension);
    }
}