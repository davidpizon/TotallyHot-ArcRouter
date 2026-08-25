using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers fenced-code-block extraction, language preference, and caps.</summary>
public class CodeBlockSignalExtractorTests
{
    private static CodeBlockSignalExtractor CreateExtractor(QualityOptions? options = null) =>
        new(
            new KeywordDimensionInferrer(),
            Options.Create(options ?? new QualityOptions()),
            NullLogger<CodeBlockSignalExtractor>.Instance);

    private static SignalExtractionContext Context(string responseText, string prompt = "generate code") =>
        new(responseText, prompt, "gpt-5.4", "corr-1", "sess-1");

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
        Assert.Equal(CodeLanguage.Python, request!.Language);
        Assert.Contains("print('hi')", request.Code, StringComparison.Ordinal);
        Assert.Equal("gpt-5.4", request.Model);
        Assert.Equal("corr-1", request.CorrelationId);
    }

    [Fact]
    public void Extract_PrefersRecognizedLanguageBlock()
    {
        var extractor = CreateExtractor();
        var response = "```\nplain text\n```\nthen\n```js\nconsole.log(1)\n```";

        var request = extractor.Extract(Context(response));

        Assert.NotNull(request);
        Assert.Equal(CodeLanguage.JavaScript, request!.Language);
    }

    [Fact]
    public void Extract_RespectsMaxCodeBytes()
    {
        var extractor = CreateExtractor(new QualityOptions { MaxCodeBytes = 5 });
        var response = "```python\n0123456789\n```";

        var request = extractor.Extract(Context(response));

        Assert.NotNull(request);
        Assert.Equal(5, request!.Code.Length);
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
        Assert.Equal(CodeLanguage.Python, request!.Language);
        Assert.Contains("print('hi')", request.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_InfersDimensionFromPrompt()
    {
        var extractor = CreateExtractor();
        var response = "```python\nx = 1\n```";

        var request = extractor.Extract(Context(response, prompt: "fix the bug in this code"));

        Assert.NotNull(request);
        Assert.Equal("bug_fixing", request!.Dimension);
    }
}

