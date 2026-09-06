using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using TotallyHot.ArcRouter.Tests.Proxy;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers Phase Q3's three portfolio grader clients (<see cref="CodeJudgeGraderClient"/>,
/// <see cref="IceScoreGraderClient"/>, <see cref="RaceGraderClient"/>) against a fake
/// <see cref="HttpMessageHandler"/> - no real network call is ever made. <see cref="PortfolioGraderClientBase"/>'s
/// shared transport (route resolution, URL, headers) is exercised once via <see cref="CodeJudgeGraderClient"/>,
/// mirroring <see cref="GEvalJudgeClientTests"/>'s own transport coverage; each grader's own scoring rule is
/// covered independently.
/// </summary>
public class PortfolioGraderClientTests
{
    // CodeJudge: deduction: negligible=0, small=5, major=50, fatal=100; score = 1 - min(100, Σ)/100.
    [Theory]
    [InlineData("FAULT: none", 1.0)]
    [InlineData("FAULT: negligible", 1.0)]
    [InlineData("FAULT: small", 0.95)]
    [InlineData("FAULT: major", 0.50)]
    [InlineData("FAULT: fatal", 0.0)]
    [InlineData("FAULT: major\nFAULT: small", 0.45)]
    [InlineData("FAULT: fatal\nFAULT: fatal", 0.0)] // capped at 100 total deduction
    public async Task CodeJudge_ScoreAsync_AppliesTheSeverityWeightedDeductionRule(string content, double expected)
    {
        var client = CreateCodeJudgeClient(BuildResponse(content));

        var score = await client.ScoreAsync(
            request: new PortfolioGraderScoreRequest(Dimension: "bug_fixing", ResponseText: "some response",
                Prompt: "fix the bug"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(score);
        Assert.Equal(expected, actual: score.Value, 6);
    }

    [Fact]
    public async Task CodeJudge_ScoreAsync_NoFaultLineAtAll_Throws()
    {
        var client = CreateCodeJudgeClient(BuildResponse("I looked at the code and it seems fine overall."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ScoreAsync(
                request: new PortfolioGraderScoreRequest(Dimension: "bug_fixing", ResponseText: "x", Prompt: "y"),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CodeJudge_GraderKey_IsCodeJudge()
    {
        Assert.Equal(expected: "codejudge", actual: new CodeJudgeGraderClient(
            httpClientFactory: new FakeHttpClientFactory(FakeHttpMessageHandler.AlwaysFails(HttpStatusCode.OK)),
            modelSelector: CreateSelector(FreeResolver()),
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            logger: NullLogger<CodeJudgeGraderClient>.Instance).GraderKey);
    }

    // ICE-Score: 0-4 usefulness digit, normalized (digit-0)/4.
    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("2", 0.5)]
    [InlineData("4", 1.0)]
    public async Task IceScore_ScoreAsync_ParsesAndNormalizesTheDigit(string content, double expected)
    {
        var client = CreateIceScoreClient(BuildResponse(content));

        var score = await client.ScoreAsync(
            request: new PortfolioGraderScoreRequest(Dimension: "code_generation", ResponseText: "x", Prompt: "y"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(score);
        Assert.Equal(expected, actual: score.Value, 6);
    }

    [Fact]
    public async Task IceScore_ScoreAsync_NoDigit_Throws()
    {
        var client = CreateIceScoreClient(BuildResponse("not a number"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ScoreAsync(
                request: new PortfolioGraderScoreRequest(Dimension: "code_generation", ResponseText: "x", Prompt: "y"),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    // RACE: 1-5 readability/maintainability digit, normalized (digit-1)/4.
    [Theory]
    [InlineData("1", 0.0)]
    [InlineData("3", 0.5)]
    [InlineData("5", 1.0)]
    public async Task Race_ScoreAsync_ParsesAndNormalizesTheDigit(string content, double expected)
    {
        var client = CreateRaceClient(BuildResponse(content));

        var score = await client.ScoreAsync(
            request: new PortfolioGraderScoreRequest(Dimension: "code_refactoring", ResponseText: "x", Prompt: "y"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(score);
        Assert.Equal(expected, actual: score.Value, 6);
    }

    /// <summary>
    /// The shared transport contract, mirroring <see cref="GEvalJudgeClientTests.ScoreAsync_SendsToResolvedProviderRoute_WithProviderModelIdAndAuthHeader"/>:
    /// every portfolio grader reaches the provider the Providers screen configured, at the correctly
    /// collapsed URL, with the upstream model id and credential header.
    /// </summary>
    [Fact]
    public async Task ScoreAsync_SendsToResolvedProviderRoute_WithProviderModelIdAndAuthHeader()
    {
        var requests = new List<CapturedRequest>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            requests.Add(new CapturedRequest(
                Url: request.RequestUri!.ToString(),
                AuthorizationHeader: request.Headers.TryGetValues(name: "Authorization", values: out var auth)
                    ? string.Join(separator: ",", values: auth)
                    : null,
                Body: request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: BuildResponse("FAULT: none"), encoding: Encoding.UTF8,
                    mediaType: "application/json")
            };
        });

        var client = new CodeJudgeGraderClient(
            httpClientFactory: new FakeHttpClientFactory(handler),
            modelSelector: CreateSelector(FreeResolver()),
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            logger: NullLogger<CodeJudgeGraderClient>.Instance);

        await client.ScoreAsync(
            request: new PortfolioGraderScoreRequest(Dimension: "bug_fixing", ResponseText: "some response",
                Prompt: "fix the bug"),
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(requests);
        Assert.Equal(expected: "http://localhost:1234/v1/chat/completions", actual: request.Url);
        Assert.Equal(expected: "Bearer judge-key", actual: request.AuthorizationHeader);
        Assert.Contains(expectedSubstring: "\"model\":\"qwen2.5-7b-instruct\"", actualString: request.Body,
            comparisonType: StringComparison.Ordinal);
    }

    /// <summary>No free provider configured is an abstention: null, no HTTP call.</summary>
    [Fact]
    public async Task ScoreAsync_NoFreeModelConfigured_ReturnsNullWithoutCallingAnything()
    {
        var paidOnly = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://api.openai.com",
            isFree: false);
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildResponse("FAULT: none"))
            });

        var client = new CodeJudgeGraderClient(
            httpClientFactory: new FakeHttpClientFactory(handler),
            modelSelector: CreateSelector(paidOnly),
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            logger: NullLogger<CodeJudgeGraderClient>.Instance);

        var score = await client.ScoreAsync(
            request: new PortfolioGraderScoreRequest(Dimension: "bug_fixing", ResponseText: "x", Prompt: "y"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(score);
    }

    private static CodeJudgeGraderClient CreateCodeJudgeClient(string responseJson)
    {
        return new CodeJudgeGraderClient(
            httpClientFactory: new FakeHttpClientFactory(HandlerReturning(responseJson)),
            modelSelector: CreateSelector(FreeResolver()),
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            logger: NullLogger<CodeJudgeGraderClient>.Instance);
    }

    private static IceScoreGraderClient CreateIceScoreClient(string responseJson)
    {
        return new IceScoreGraderClient(
            httpClientFactory: new FakeHttpClientFactory(HandlerReturning(responseJson)),
            modelSelector: CreateSelector(FreeResolver()),
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            logger: NullLogger<IceScoreGraderClient>.Instance);
    }

    private static RaceGraderClient CreateRaceClient(string responseJson)
    {
        return new RaceGraderClient(
            httpClientFactory: new FakeHttpClientFactory(HandlerReturning(responseJson)),
            modelSelector: CreateSelector(FreeResolver()),
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            logger: NullLogger<RaceGraderClient>.Instance);
    }

    private static FakeHttpMessageHandler HandlerReturning(string responseJson)
    {
        return new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content: responseJson, encoding: Encoding.UTF8, mediaType: "application/json")
        });
    }

    private static string BuildResponse(string content)
    {
        return $$"""{ "choices": [ { "message": { "content": {{System.Text.Json.JsonSerializer.Serialize(content)}} } } ] }""";
    }

    private static JudgeModelSelector CreateSelector(IModelRouteResolver resolver)
    {
        return new JudgeModelSelector(routeResolver: resolver,
            options: new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            logger: NullLogger<JudgeModelSelector>.Instance);
    }

    private static IModelRouteResolver FreeResolver()
    {
        return ModelRouteResolverTestFactory.Create(
            modelName: "local-judge",
            providerModelId: "qwen2.5-7b-instruct",
            baseUrl: "http://localhost:1234/v1",
            apiKey: "judge-key",
            providerName: "lmstudio",
            isFree: true);
    }

    private sealed record CapturedRequest(string Url, string? AuthorizationHeader, string Body);
}
