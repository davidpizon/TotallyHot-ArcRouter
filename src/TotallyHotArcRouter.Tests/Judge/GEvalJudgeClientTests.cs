using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using TotallyHot.ArcRouter.Tests.Proxy;
using TotallyHot.ArcRouter.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Covers <see cref="GEvalJudgeClient"/> against a fake <see cref="HttpMessageHandler"/> - no real network
/// call is ever made. Exercises the probability-weighted G-Eval parse, the single-sample fallback when no
/// logprobs are present, and the failure path when neither is parseable.
/// </summary>
public class GEvalJudgeClientTests
{
    [Fact]
    public async Task ScoreAsync_ResponseWithLogprobs_ComputesProbabilityWeightedScore()
    {
        // Sampled token "4" (logprob 0 -> p=1) with a "3" alternative (logprob -1 -> p=e^-1=0.36788).
        // Weighted mean = (4*1 + 3*0.36788) / (1 + 0.36788) = 3.73113 -> normalized (3.73113-1)/4 = 0.68278.
        var json = """
            {
              "choices": [
                {
                  "message": { "content": "4" },
                  "logprobs": {
                    "content": [
                      {
                        "token": "4",
                        "logprob": 0.0,
                        "top_logprobs": [
                          { "token": "4", "logprob": 0.0 },
                          { "token": "3", "logprob": -1.0 }
                        ]
                      }
                    ]
                  }
                }
              ]
            }
            """;

        var client = CreateClient(json);

        var result = await client.ScoreAsync(new JudgeScoreRequest("algorithm", "some response"), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result.UsedLogprobs);
        Assert.InRange(result.Score, 0.68, 0.69);
    }

    [Fact]
    public async Task ScoreAsync_ResponseWithNoLogprobs_FallsBackToSingleSampleParse()
    {
        var json = """
            {
              "choices": [
                { "message": { "content": "The score is 4." } }
              ]
            }
            """;

        var client = CreateClient(json);

        var result = await client.ScoreAsync(new JudgeScoreRequest("algorithm", "some response"), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.UsedLogprobs);
        // Digit "4" normalized: (4-1)/4 = 0.75.
        Assert.Equal(0.75, result.Score, 6);
    }

    [Fact]
    public async Task ScoreAsync_NoParseableScoreAnywhere_Throws()
    {
        var json = """
            {
              "choices": [
                { "message": { "content": "I cannot answer that." } }
              ]
            }
            """;

        var client = CreateClient(json);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ScoreAsync(new JudgeScoreRequest("algorithm", "some response"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScoreAsync_NoChoices_Throws()
    {
        var client = CreateClient("""{ "choices": [] }""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ScoreAsync(new JudgeScoreRequest("algorithm", "some response"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScoreAsync_NonSuccessStatusCode_Throws()
    {
        var handler = FakeHttpMessageHandler.AlwaysFails(HttpStatusCode.InternalServerError);
        var client = new GEvalJudgeClient(
            new FakeHttpClientFactory(handler),
            CreateSelector(FreeResolver()),
            new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions()),
            NullLogger<GEvalJudgeClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ScoreAsync(new JudgeScoreRequest("algorithm", "some response"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The transport contract: the judge reaches the provider the Providers screen configured, at the
    /// correctly collapsed URL, sending that provider's upstream model id and credential header. The
    /// <c>/v1</c>-suffixed base URL is the case ProviderUrlBuilder exists for - plain concatenation would
    /// produce <c>/v1/v1/chat/completions</c>, which LM Studio answers 200 with an error body, making the
    /// mistake invisible to every downstream check.
    /// </summary>
    [Fact]
    public async Task ScoreAsync_SendsToResolvedProviderRoute_WithProviderModelIdAndAuthHeader()
    {
        var client = CreateClient("""{ "choices": [ { "message": { "content": "4" } } ] }""", out var captured, FreeResolver());

        var result = await client.ScoreAsync(new JudgeScoreRequest("algorithm", "some response"), TestContext.Current.CancellationToken);

        var request = Assert.Single(captured);
        Assert.Equal("http://localhost:1234/v1/chat/completions", request.Url);
        Assert.Equal("Bearer judge-key", request.AuthorizationHeader);
        Assert.Contains("\"model\":\"qwen2.5-7b-instruct\"", request.Body, StringComparison.Ordinal);

        // The row is stamped with the client-facing name, not the upstream id.
        Assert.Equal("local-judge", result!.JudgeModel);
    }

    /// <summary>
    /// No free provider configured is an abstention, not a failure: the client returns null and never makes
    /// an HTTP call, so the drain worker records nothing rather than a fabricated score.
    /// </summary>
    [Fact]
    public async Task ScoreAsync_NoFreeModelConfigured_ReturnsNullWithoutCallingAnything()
    {
        var paidOnly = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://api.openai.com",
            isFree: false);

        var client = CreateClient("""{ "choices": [] }""", out var captured, paidOnly);

        var result = await client.ScoreAsync(new JudgeScoreRequest("algorithm", "some response"), TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(captured);
    }

    private static GEvalJudgeClient CreateClient(string responseJson) =>
        CreateClient(responseJson, out _, FreeResolver());

    /// <summary>
    /// Builds a client over a fake handler, exposing the requests it received so a test can assert on the
    /// URL, body, and headers the judge actually sent.
    /// </summary>
    private static GEvalJudgeClient CreateClient(
        string responseJson,
        out List<CapturedRequest> captured,
        IModelRouteResolver resolver,
        JudgeOptions? options = null)
    {
        var requests = new List<CapturedRequest>();
        captured = requests;

        // The body is read here, inside the handler, rather than from the HttpRequestMessage afterwards:
        // GEvalJudgeClient disposes the request (and with it its JsonContent) as soon as the call returns,
        // so a deferred read would hit an ObjectDisposedException.
        var handler = new FakeHttpMessageHandler(request =>
        {
            requests.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("Authorization", out var auth) ? string.Join(",", auth) : null,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        });

        return new GEvalJudgeClient(
            new FakeHttpClientFactory(handler),
            CreateSelector(resolver, options),
            new StaticOptionsMonitor<JudgeOptions>(options ?? new JudgeOptions()),
            NullLogger<GEvalJudgeClient>.Instance);
    }

    /// <summary>One outbound judge call, snapshotted while the request is still alive.</summary>
    /// <param name="Url">The absolute URL the judge posted to.</param>
    /// <param name="AuthorizationHeader">The Authorization header value, or null when none was sent.</param>
    /// <param name="Body">The serialized request body.</param>
    private sealed record CapturedRequest(string Url, string? AuthorizationHeader, string Body);

    /// <summary>A selector over <paramref name="resolver"/>, resolving whichever free model it exposes.</summary>
    private static JudgeModelSelector CreateSelector(IModelRouteResolver resolver, JudgeOptions? options = null) =>
        new(resolver,
            new StaticOptionsMonitor<JudgeOptions>(options ?? new JudgeOptions()),
            NullLogger<JudgeModelSelector>.Instance);

    /// <summary>
    /// A resolver exposing one free model. The base URL deliberately carries a <c>/v1</c> suffix - the
    /// LM Studio shape whose naive concatenation with the request path produced
    /// <c>/v1/v1/chat/completions</c> before ProviderUrlBuilder handled it.
    /// </summary>
    private static IModelRouteResolver FreeResolver() =>
        ModelRouteResolverTestFactory.Create(
            modelName: "local-judge",
            providerModelId: "qwen2.5-7b-instruct",
            baseUrl: "http://localhost:1234/v1",
            apiKey: "judge-key",
            providerName: "lmstudio",
            isFree: true);
}
