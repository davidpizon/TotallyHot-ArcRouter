using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GraderQuestionText"/>: the shared fail-closed contract every LLM grader uses so a
/// missing user/task question cannot silently become response-only scoring (GitHub issue #114).
/// </summary>
public class GraderQuestionTextTests
{
    [Theory]
    [InlineData("write a function that reverses a string")]
    [InlineData(" fix the off-by-one ")]
    public void IsPresent_NonEmptyQuestion_IsTrue(string question)
    {
        Assert.True(GraderQuestionText.IsPresent(question));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void IsPresent_MissingOrWhitespace_IsFalse(string? question)
    {
        Assert.False(GraderQuestionText.IsPresent(question));
    }

    [Fact]
    public void Require_PresentQuestion_ReturnsTrimmedText()
    {
        Assert.Equal(expected: "sort a list in place",
            actual: GraderQuestionText.Require("  sort a list in place  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Require_MissingQuestion_Throws(string? question)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GraderQuestionText.Require(question));
        Assert.Contains(expectedSubstring: "without the user/task question", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTaskSection_PresentQuestion_WeavesTheTaskIn()
    {
        var section = GraderQuestionText.FormatTaskSection("write a function that reverses a string");

        Assert.Contains(expectedSubstring: "Task the response was written for", actualString: section,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "write a function that reverses a string", actualString: section,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTaskSection_MissingQuestion_ThrowsRatherThanOmittingTheSection()
    {
        Assert.Throws<InvalidOperationException>(() => GraderQuestionText.FormatTaskSection("   "));
    }

    [Fact]
    public void MissingReason_PrefixesTheSharedToken()
    {
        Assert.Equal(expected: "judge-question-missing", actual: GraderQuestionText.MissingReason("judge"));
        Assert.Equal(expected: "codejudge-question-missing",
            actual: GraderQuestionText.MissingReason(GraderKeys.CodeJudge));
    }
}
