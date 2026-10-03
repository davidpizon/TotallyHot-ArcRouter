using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation;
using TotallyHot.ArcRouter.Router;
using static TotallyHot.ArcRouter.Tests.Proxy.ModelFeatureSupportFixtures;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// End-to-end coverage of ADR-0022 Amendment 1 through <see cref="ProxyMiddleware"/> on native <c>/v1/messages</c>
/// traffic: what actually reaches the upstream (body and <c>anthropic-beta</c>), the
/// <c>X-ArcRouter-Stripped-Features</c> header, and a failover from a stripped candidate to a capable one that must
/// receive the request as the client sent it.
/// </summary>
public sealed class ProxyMiddlewareFeatureStripTests
{
    // Both models sit on the one "anthropic" provider, so they share a base URL; a forward is identified by the
    // upstream model id in its body instead.
    private const string HaikuId = "claude-haiku-4-5-20251001";
    private const string SonnetId = "claude-sonnet-5";

    private const string MessageResponse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"ok"}],
         "stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
        """;

    private sealed record Forwarded(string Model, JsonObject Body, string? Beta);

    private static (ProxyMiddleware Middleware, List<Forwarded> Forwards) Build(string policyPick,
        HttpStatusCode haikuStatus = HttpStatusCode.OK)
    {
        var forwards = new List<Forwarded>();
        var handler = new DelegatingHandlerStub(async request =>
        {
            var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            var beta = request.Headers.TryGetValues(name: "anthropic-beta", values: out var values)
                ? string.Join(separator: ",", values: values)
                : null;
            var model = body["model"]!.GetValue<string>();
            forwards.Add(new Forwarded(Model: model, Body: body, Beta: beta));

            var status = model == HaikuId ? haikuStatus : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(content: status == HttpStatusCode.OK ? MessageResponse : "{}",
                    encoding: Encoding.UTF8, mediaType: "application/json")
            };
        });

        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("claude-haiku", "anthropic", HaikuId, "https://api.anthropic.test"),
            ("claude-sonnet", "anthropic", SonnetId, "https://api.anthropic.test"));
        var interceptor = new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: resolver, routingPolicy: new FixedPolicy(policyPick),
            modelFeatureSupportStore: new FakeModelFeatureSupportStore().With(Haiku45(), Capable(SonnetId)));

        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                Translators = new Dictionary<string, IPayloadTranslator>(StringComparer.OrdinalIgnoreCase)
                {
                    ["anthropic"] = new AnthropicPayloadTranslator()
                }
            });

        return (middleware, forwards);
    }

    private static DefaultHttpContext Context(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/messages";
        context.Request.Headers["anthropic-beta"] = CapturedBetaHeader;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<DefaultHttpContext> Send(ProxyMiddleware middleware, string body)
    {
        var context = Context(body);
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);
        return context;
    }

    [Fact]
    public async Task AutoPickOfHaiku_ForwardsTheStrippedCopy_WithItsBetaValuesRemoved()
    {
        var (middleware, forwards) = Build(policyPick: "claude-haiku");

        var context = await Send(middleware, ExploreBody);

        var forwarded = Assert.Single(forwards);
        Assert.Equal(expected: HaikuId, actual: forwarded.Model);
        Assert.Null(forwarded.Body["thinking"]);
        Assert.Null(forwarded.Body["output_config"]);
        Assert.Null(forwarded.Body["context_management"]);
        Assert.NotNull(forwarded.Body["messages"]);

        Assert.NotNull(forwarded.Beta);
        Assert.DoesNotContain(expectedSubstring: "effort-", actualString: forwarded.Beta);
        Assert.DoesNotContain(expectedSubstring: "context-management-", actualString: forwarded.Beta);
        Assert.Contains(expectedSubstring: "interleaved-thinking-2025-05-14", actualString: forwarded.Beta);
        Assert.Contains(expectedSubstring: "claude-code-20250219", actualString: forwarded.Beta);

        Assert.Equal(
            expected:
            "thinking.adaptive, output_config.effort, context_management.clear_thinking_20251015, context_management",
            actual: context.Response.Headers[ProxyMiddleware.StrippedFeaturesHeaderName].ToString());
    }

    [Fact]
    public async Task AutoPickOfACapableModel_ForwardsTheRequestAsSent_WithNoStripHeader()
    {
        var (middleware, forwards) = Build(policyPick: "claude-sonnet");

        var context = await Send(middleware, ExploreBody);

        var forwarded = Assert.Single(forwards);
        Assert.Equal(expected: SonnetId, actual: forwarded.Model);
        Assert.NotNull(forwarded.Body["thinking"]);
        Assert.Equal(expected: CapturedBetaHeader, actual: forwarded.Beta);
        Assert.False(context.Response.Headers.ContainsKey(ProxyMiddleware.StrippedFeaturesHeaderName));
    }

    [Fact]
    public async Task FailoverFromAStrippedHaiku_SendsTheCapableFallbackTheFullRequest()
    {
        var (middleware, forwards) = Build(policyPick: "claude-haiku", haikuStatus: HttpStatusCode.ServiceUnavailable);

        var context = await Send(middleware, ExploreBody);

        Assert.Equal(2, actual: forwards.Count);
        Assert.Equal(expected: HaikuId, actual: forwards[0].Model);
        Assert.Null(forwards[0].Body["thinking"]);

        var fallback = forwards[1];
        Assert.Equal(expected: SonnetId, actual: fallback.Model);
        Assert.Equal(expected: "adaptive", actual: fallback.Body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(expected: "high", actual: fallback.Body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal(expected: CapturedBetaHeader, actual: fallback.Beta);

        // The header describes the candidate that answered, which stripped nothing.
        Assert.False(context.Response.Headers.ContainsKey(ProxyMiddleware.StrippedFeaturesHeaderName));
    }

    [Fact]
    public async Task ExplicitPickOfHaiku_IsForwardedAsSent()
    {
        var (middleware, forwards) = Build(policyPick: "claude-sonnet");
        var body = (JsonObject)JsonNode.Parse(ExploreBody)!;
        body["model"] = "claude-haiku";

        var context = await Send(middleware, body.ToJsonString());

        var forwarded = Assert.Single(forwards);
        Assert.Equal(expected: HaikuId, actual: forwarded.Model);
        Assert.NotNull(forwarded.Body["thinking"]);
        Assert.Equal(expected: CapturedBetaHeader, actual: forwarded.Beta);
        Assert.False(context.Response.Headers.ContainsKey(ProxyMiddleware.StrippedFeaturesHeaderName));
    }

    private sealed class FixedPolicy(string selection) : IRoutingPolicy
    {
        public Task<string> SelectModelAsync(RoutingContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(selection);
        }
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}
