using TotallyHot.ArcRouter.Quality.Extraction;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers the keyword dimension inferrer.</summary>
public class KeywordDimensionInferrerTests
{
    private readonly KeywordDimensionInferrer _inferrer = new();

    [Theory]
    [InlineData("Please fix this bug in my function", "bug_fixing")]
    [InlineData("Refactor this to be cleaner", "code_refactoring")]
    [InlineData("Write unit tests for this class", "test_generation")]
    [InlineData("Explain what does this code do", "code_understanding")]
    [InlineData("Optimize the sorting algorithm complexity", "algorithm_design")]
    [InlineData("Load the dataset into a pandas dataframe", "data_science")]
    [InlineData("Complete this function for me", "code_completion")]
    [InlineData("Generate a REST endpoint", "code_generation")]
    [InlineData("", "code_generation")]
    [InlineData("Port this algorithm from Python to Go", "multi_language")]
    [InlineData("Translate this Python snippet to JavaScript", "multi_language")]
    [InlineData("Rewrite this Ruby class in Kotlin", "multi_language")]
    [InlineData("I need this to go faster in Python", "code_generation")]
    [InlineData("Make this code go really fast", "code_generation")]
    [InlineData("Rewrite this function in place to avoid the extra allocation", "code_generation")]
    public void Infer_ReturnsExpectedDimension(string prompt, string expected)
    {
        Assert.Equal(expected: expected, actual: _inferrer.Infer(prompt: prompt, language: CodeLanguage.Python));
    }

    [Fact]
    public void Infer_NullPrompt_DefaultsToCodeGeneration()
    {
        Assert.Equal(expected: "code_generation",
            actual: _inferrer.Infer(prompt: null!, language: CodeLanguage.Unknown));
    }
}