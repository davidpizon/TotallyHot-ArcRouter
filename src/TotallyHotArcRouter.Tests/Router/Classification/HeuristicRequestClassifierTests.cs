using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Router.Classification;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.Router.Classification;

/// <summary>Tests for <see cref="HeuristicRequestClassifier"/> - PLAN.md Phase H.</summary>
public class HeuristicRequestClassifierTests
{
    private readonly HeuristicRequestClassifier _classifier = new();

    private static JsonObject RequestWithPrompt(string prompt, int? maxTokens = null)
    {
        var body = new JsonObject
        {
            ["model"] = "auto",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt },
            },
        };

        if (maxTokens is not null)
        {
            body["max_tokens"] = maxTokens.Value;
        }

        return body;
    }

    [Theory]
    [InlineData("Please fix this bug in my function", RouterDimension.BugFixing)]
    [InlineData("Refactor this to be cleaner", RouterDimension.CodeRefactoring)]
    [InlineData("Write unit tests for this class", RouterDimension.TestGeneration)]
    [InlineData("Explain what does this code do", RouterDimension.CodeUnderstanding)]
    [InlineData("Optimize the sorting algorithm complexity", RouterDimension.AlgorithmDesign)]
    [InlineData("Load the dataset into a pandas dataframe", RouterDimension.DataScience)]
    [InlineData("Complete this function for me", RouterDimension.CodeCompletion)]
    [InlineData("Generate a REST endpoint", RouterDimension.CodeGeneration)]
    [InlineData("Port this algorithm from Python to Go", RouterDimension.MultiLanguage)]
    public void Classify_AllNineDimensions_MatchTheSharedInferrer(string prompt, string expectedDimension)
    {
        var result = _classifier.Classify(RequestWithPrompt(prompt));

        Assert.Equal(expectedDimension, result.Dimension);
    }

    [Fact]
    public void Classify_AllDimensions_AreReachableThroughTheClassifier()
    {
        var reachable = new HashSet<string>
        {
            _classifier.Classify(RequestWithPrompt("Please fix this bug in my function")).Dimension,
            _classifier.Classify(RequestWithPrompt("Refactor this to be cleaner")).Dimension,
            _classifier.Classify(RequestWithPrompt("Write unit tests for this class")).Dimension,
            _classifier.Classify(RequestWithPrompt("Explain what does this code do")).Dimension,
            _classifier.Classify(RequestWithPrompt("Optimize the sorting algorithm complexity")).Dimension,
            _classifier.Classify(RequestWithPrompt("Load the dataset into a pandas dataframe")).Dimension,
            _classifier.Classify(RequestWithPrompt("Complete this function for me")).Dimension,
            _classifier.Classify(RequestWithPrompt("Generate a REST endpoint")).Dimension,
            _classifier.Classify(RequestWithPrompt("Port this algorithm from Python to Go")).Dimension,
        };

        Assert.Equal(RouterDimension.AllDimensions.Count, reachable.Count);
        foreach (var dimension in RouterDimension.AllDimensions)
        {
            Assert.Contains(dimension, reachable);
        }
    }

    [Fact]
    public void Classify_ShortPrompt_IsEasy()
    {
        var result = _classifier.Classify(RequestWithPrompt("Add a null check here"));

        Assert.Equal(RouterDifficulty.Easy, result.Difficulty);
    }

    [Fact]
    public void Classify_PromptNamingConcurrencySignal_IsHard()
    {
        var prompt = "Please review this thread-safe cache for a subtle race condition under high concurrency load.";
        var result = _classifier.Classify(RequestWithPrompt(prompt));

        Assert.Equal(RouterDifficulty.Hard, result.Difficulty);
    }

    [Fact]
    public void Classify_LongPromptWithNoHardSignal_IsHardOnLengthAlone()
    {
        var prompt = string.Concat(Enumerable.Repeat("Please generate a function that adds two numbers. ", 40));
        var result = _classifier.Classify(RequestWithPrompt(prompt));

        Assert.Equal(RouterDifficulty.Hard, result.Difficulty);
    }

    [Fact]
    public void Classify_MediumLengthPromptWithNoSignal_IsMedium()
    {
        var prompt = string.Concat(Enumerable.Repeat("Please generate a function that adds two numbers. ", 6));
        var result = _classifier.Classify(RequestWithPrompt(prompt));

        Assert.Equal(RouterDifficulty.Medium, result.Difficulty);
    }

    [Fact]
    public void Classify_SmallMaxTokens_IsUtility()
    {
        var result = _classifier.Classify(RequestWithPrompt("Summarize this into a chat title", maxTokens: 16));

        Assert.True(result.IsUtility);
    }

    [Fact]
    public void Classify_ShortPromptNamingUtilityWorkflow_IsUtility()
    {
        var result = _classifier.Classify(RequestWithPrompt("Generate a chat title for this conversation"));

        Assert.True(result.IsUtility);
    }

    [Fact]
    public void Classify_NormalChatPrompt_IsNotUtility()
    {
        var result = _classifier.Classify(RequestWithPrompt("Write a function that reverses a linked list in place."));

        Assert.False(result.IsUtility);
    }

    [Fact]
    public void Classify_FencedPythonBlock_DetectsLanguage()
    {
        var prompt = "Here is my code:\n```python\ndef f():\n    pass\n```\nplease fix the bug";
        var result = _classifier.Classify(RequestWithPrompt(prompt));

        Assert.Equal("python", result.Language);
    }

    [Fact]
    public void Classify_NoFencedBlock_LanguageIsUnknown()
    {
        var result = _classifier.Classify(RequestWithPrompt("Generate a REST endpoint"));

        Assert.Equal("unknown", result.Language);
    }

    [Fact]
    public void Classify_NullRequestBody_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _classifier.Classify(null!));
    }
}
