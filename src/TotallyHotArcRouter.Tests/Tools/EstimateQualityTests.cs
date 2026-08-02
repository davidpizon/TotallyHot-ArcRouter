using TotallyHot.ArcRouter.Tools;

namespace TotallyHot.ArcRouter.Tests.Tools;

/// <summary>
/// Covers quality estimation behavior.
/// </summary>
public class EstimateQualityTests
{
    [Fact]
    public void Estimate_WithEmptyCode_ReturnsZero()
    {
        var estimateQuality = new EstimateQuality();

        var score = estimateQuality.Estimate(string.Empty);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Estimate_WithWhitespaceCode_ReturnsZero()
    {
        var estimateQuality = new EstimateQuality();

        var score = estimateQuality.Estimate("   \r\n\t");

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Estimate_WithShortCode_ReturnsLowScore()
    {
        var estimateQuality = new EstimateQuality();

        var score = estimateQuality.Estimate("public class A {}");

        Assert.True(score > 0.0);
        Assert.True(score < 0.5);
    }

    [Fact]
    public void Estimate_WithLongerCodeAndNoComments_IsPenalized()
    {
        var estimateQuality = new EstimateQuality();
        var codeWithComment = @"
// This is a good class
public class MyClass
{
    public void MyMethod() {}
}";
        var codeWithoutComment = @"
public class MyClass
{
    public void MyMethod() {}
}";

        var scoreWithComment = estimateQuality.Estimate(codeWithComment);
        var scoreWithoutComment = estimateQuality.Estimate(codeWithoutComment);

        Assert.True(scoreWithoutComment < scoreWithComment);
    }

    [Fact]
    public void Estimate_WithVeryLongCode_RemainsInExpectedRange()
    {
        var estimateQuality = new EstimateQuality();
        var longCode = string.Concat(Enumerable.Repeat("public class A { }\n", 1000));

        var score = estimateQuality.Estimate(longCode);

        Assert.InRange(score, 0.0, 1.0);
    }
}

