using Moq;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="JudgeGraderClient"/>, the adapter that lets the shared grader drain call G-Eval
/// through <see cref="IPortfolioGraderClient"/>. The adapter is pure field mapping, so a swapped or dropped
/// field would silently corrupt every judge row; these tests pin each one in both directions.
/// </summary>
public class JudgeGraderClientTests
{
    [Fact]
    public void GraderKey_IsTheJudgeKey()
    {
        var client = new JudgeGraderClient(Mock.Of<IJudgeClient>());

        Assert.Equal(expected: GraderKeys.Judge, actual: client.GraderKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScoreAsync_MapsRequestAndResultFields(bool usedLogprobs)
    {
        JudgeScoreRequest? forwarded = null;
        var judge = new Mock<IJudgeClient>();
        judge.Setup(j => j.ScoreAsync(It.IsAny<JudgeScoreRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JudgeScoreRequest, CancellationToken>((request, _) => forwarded = request)
            .ReturnsAsync(new JudgeScoreResult(0.8, usedLogprobs, JudgeModel: "free-judge-model"));
        var client = new JudgeGraderClient(judge.Object);

        var result = await client.ScoreAsync(
            request: new PortfolioGraderScoreRequest(Dimension: "algorithm", ResponseText: "the response",
                Prompt: "write a function that adds two numbers"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(forwarded);
        Assert.Equal(expected: "algorithm", actual: forwarded.Dimension);
        Assert.Equal(expected: "the response", actual: forwarded.ResponseText);
        Assert.Equal(expected: "write a function that adds two numbers", actual: forwarded.Prompt);

        Assert.NotNull(result);
        Assert.Equal(0.8, actual: result.Score);
        Assert.Equal(expected: "free-judge-model", actual: result.GraderModel);
        Assert.Equal(expected: usedLogprobs, actual: result.UsedLogprobs);
    }

    /// <summary>A judge abstention (no eligible backbone) must stay an abstention, not become a score.</summary>
    [Fact]
    public async Task ScoreAsync_JudgeAbstains_ReturnsNull()
    {
        var judge = new Mock<IJudgeClient>();
        judge.Setup(j => j.ScoreAsync(It.IsAny<JudgeScoreRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JudgeScoreResult?)null);
        var client = new JudgeGraderClient(judge.Object);

        var result = await client.ScoreAsync(
            request: new PortfolioGraderScoreRequest(Dimension: "algorithm", ResponseText: "the response",
                Prompt: "write a function that adds two numbers"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }
}
