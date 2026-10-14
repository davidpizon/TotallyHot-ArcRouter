using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>Covers the issue #293 judge rubric and RouterDimension hints in <see cref="GEvalJudgeClient"/>.</summary>
public class GEvalJudgePromptTests
{
    [Fact]
    public void BuildPrompt_ContainsAllThreeCriteria()
    {
        var prompt = GEvalJudgeClient.BuildPrompt(
            dimension: RouterDimension.BugFixing,
            responseText: "fixed it",
            taskPrompt: "fix the null ref");

        Assert.Contains("Relevance", prompt, StringComparison.Ordinal);
        Assert.Contains("Correctness", prompt, StringComparison.Ordinal);
        Assert.Contains("Completeness", prompt, StringComparison.Ordinal);
        Assert.Contains("fix the null ref", prompt, StringComparison.Ordinal);
        Assert.Contains("MUST score 1 or 2", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllRouterDimensions))]
    public void ResolveDimensionHint_EveryRouterDimension_HasDedicatedHint(string dimension)
    {
        var hint = GEvalJudgeClient.ResolveDimensionHint(dimension);

        Assert.False(string.IsNullOrWhiteSpace(hint));
        Assert.DoesNotContain("coding-task answer", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDimensionHint_UnknownDimension_UsesGenericEmphasis()
    {
        var hint = GEvalJudgeClient.ResolveDimensionHint("not_a_real_dimension");

        Assert.Contains("coding-task", hint, StringComparison.Ordinal);
    }

    /// <summary>Supplies every <see cref="RouterDimension.AllDimensions"/> value as theory data.</summary>
    public static TheoryData<string> AllRouterDimensions()
    {
        var data = new TheoryData<string>();
        foreach (var dimension in RouterDimension.AllDimensions) data.Add(dimension);
        return data;
    }
}
