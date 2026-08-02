using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Extraction;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

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
    public void Infer_ReturnsExpectedDimension(string prompt, string expected)
    {
        Assert.Equal(expected, _inferrer.Infer(prompt, SandboxLanguage.Python));
    }

    [Fact]
    public void Infer_NullPrompt_DefaultsToCodeGeneration()
    {
        Assert.Equal("code_generation", _inferrer.Infer(null!, SandboxLanguage.Unknown));
    }
}

