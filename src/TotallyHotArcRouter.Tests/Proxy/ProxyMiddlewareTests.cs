using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers request forwarding behavior for <see cref="ProxyMiddleware"/>.
/// </summary>
public class ProxyMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_KnownModel_ForwardsToResolvedUpstream_RewritesBody_AndInjectsCredential()
    {
        var loggerMock = new Mock<ILogger<ProxyMiddleware>>();
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com",
            authHeaderName: "Authorization",
            authHeaderScheme: "Bearer",
            apiKey: "secret-key");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(async request =>
        {
            Assert.Equal(expected: HttpMethod.Post, actual: request.Method);
            Assert.Equal(expected: "https://example.com/chat?x=1", actual: request.RequestUri!.ToString());
            Assert.True(request.Headers.Contains("X-Trace"));
            Assert.Equal(expected: "Bearer secret-key", actual: request.Headers.GetValues("Authorization").Single());

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(expected: "gpt-5.4-2026-01", actual: document.RootElement.GetProperty("model").GetString());

            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(content: "forwarded", encoding: Encoding.UTF8, mediaType: "text/plain")
            };
            response.Headers.Add(name: "X-From-Upstream", value: "true");
            return response;
        });

        var middleware = new ProxyMiddleware(logger: loggerMock.Object, interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        context.Request.QueryString = new QueryString("?x=1");
        context.Request.Headers["X-Trace"] = "abc";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status202Accepted, actual: context.Response.StatusCode);
        Assert.Equal(expected: "true", actual: context.Response.Headers["X-From-Upstream"].ToString());
        Assert.Equal(1, actual: interceptor.InterceptedRequestCount);

        // A named, servable model with no substitution: requested equals routed, reason is None
        // (docs/router/orchestrator-live-path-plan.md §M2.2).
        Assert.Equal(expected: "gpt-5.4", actual: context.Response.Headers["X-ArcRouter-Requested-Model"].ToString());
        Assert.Equal(expected: "gpt-5.4", actual: context.Response.Headers["X-ArcRouter-Routed-Model"].ToString());
        Assert.Equal(expected: RoutingSubstitutionReason.None.ToString(),
            actual: context.Response.Headers["X-ArcRouter-Substitution-Reason"].ToString());

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        var responseBody = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected: "forwarded", actual: responseBody);

        loggerMock.Verify(
            expression: logger => logger.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("Proxy middleware caught request to", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times: Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_NeverForwardsTheClientsInboundAuthorizationHeader_ForNonAuthorizationProviders()
    {
        // Regression test: a BYOK client (e.g. an IDE extension) sends its own placeholder "Authorization"
        // header to satisfy its own client library, not knowing it's talking to Anthropic. Providers whose
        // AuthHeaderName is something other than "Authorization" (e.g. Anthropic's "x-api-key") must never
        // forward that client header upstream alongside the injected credential - some upstreams reject the
        // request outright ("Invalid Anthropic API Key") when both a bogus Authorization and a valid
        // x-api-key are present.
        var loggerMock = new Mock<ILogger<ProxyMiddleware>>();
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "claude-sonnet-5",
            providerModelId: "claude-sonnet-5",
            baseUrl: "https://api.anthropic.com",
            authHeaderName: "x-api-key",
            authHeaderScheme: "",
            apiKey: "real-anthropic-key");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(request =>
        {
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.Equal(expected: "real-anthropic-key", actual: request.Headers.GetValues("x-api-key").Single());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: "{}", encoding: Encoding.UTF8, mediaType: "application/json")
            });
        });

        var middleware = new ProxyMiddleware(logger: loggerMock.Object, interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/messages";
        context.Request.Headers["Authorization"] = "Bearer client-placeholder-token";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"claude-sonnet-5"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ProviderAuthHeaderConfiguredButUnresolved_StripsClientsHeaderAndForwardsNoCredential()
    {
        // Regression test: a provider whose configuration declares an auth header (here, sourced from an
        // env var that happens to be unset at request time) must still have a client-sent header of that
        // same name stripped. Before route.AuthHeaderConfigured existed, whether the client's header was
        // stripped depended on whether the provider's own header happened to resolve this request - so a
        // missing env var would let a client-supplied credential slip through unmodified instead of the
        // request failing closed with no credential at all. Uses a non-"Authorization" header name because
        // "Authorization" itself is unconditionally stripped by AlwaysSkippedRequestHeaders regardless of
        // this logic - the conditional only matters for a provider like this one.
        var loggerMock = new Mock<ILogger<ProxyMiddleware>>();
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "claude-sonnet-5",
            providerModelId: "claude-sonnet-5",
            baseUrl: "https://api.anthropic.com",
            authHeaderName: "x-api-key",
            apiKey: null,
            headers: [new ProviderHeader { Name = "x-api-key", ValueEnvVar = "PROXY_MIDDLEWARE_TESTS_UNSET_VAR" }]);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(request =>
        {
            Assert.False(request.Headers.Contains("x-api-key"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: "{}", encoding: Encoding.UTF8, mediaType: "application/json")
            });
        });

        var middleware = new ProxyMiddleware(logger: loggerMock.Object, interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/messages";
        context.Request.Headers["x-api-key"] = "client-supplied-token";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"claude-sonnet-5"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ProviderWithNoAuthHeaderConfigured_ForwardsTheClientsHeaderUnmodified()
    {
        // Regression test: an unauthenticated provider (e.g. a free local runtime) declares no header
        // matching AuthHeaderName at all, so route.AuthHeaderConfigured is false and a client's own header
        // of that name must pass through untouched rather than being dropped with nothing to replace it.
        var loggerMock = new Mock<ILogger<ProxyMiddleware>>();
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "local-model",
            providerModelId: "local-model",
            baseUrl: "http://localhost:11434",
            authHeaderName: "x-api-key",
            apiKey: null);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(request =>
        {
            Assert.Equal(expected: "client-supplied-token", actual: request.Headers.GetValues("x-api-key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: "{}", encoding: Encoding.UTF8, mediaType: "application/json")
            });
        });

        var middleware = new ProxyMiddleware(logger: loggerMock.Object, interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        context.Request.Headers["x-api-key"] = "client-supplied-token";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"local-model"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotForwardToTheProxysOwnAddress_EvenWhenRequestHostMatchesIt()
    {
        // Regression test: the forwarding target must come from the resolved upstream route, never from
        // context.Request.Host, otherwise the proxy would forward a request back to itself indefinitely.
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(request =>
        {
            Assert.Equal(expected: "api.openai.com", actual: request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        });

        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_PassthroughProviderWithVersionedBaseUrl_DoesNotForwardTheVersionSegmentTwice()
    {
        // Regression test for a silent failure, not a loud one - which is why it asserts the exact URL
        // rather than a status code. LM Studio and Ollama are both documented with a "/v1" base URL, and
        // every OpenAI-shaped client sends "/v1/chat/completions"; concatenating the two forwarded to
        // "/v1/v1/chat/completions". Verified against a live LM Studio on 127.0.0.1:1234: that URL comes
        // back HTTP *200* carrying {"error":"Unexpected endpoint or method. ..."}, so ProxyMiddleware
        // records a success, the circuit breaker never trips, telemetry reports a healthy provider, and the
        // only symptom is an error-shaped body the client cannot distinguish from a completion. A 404 would
        // at least have failed loudly. See ProviderUrlBuilder.BuildPassthroughUrl.
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "qwen3-coder",
            providerModelId: "qwen3-coder-30b",
            baseUrl: "http://127.0.0.1:1234/v1",
            providerName: "lmstudio",
            apiKey: null);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        Uri? forwardedUri = null;
        var handler = new DelegatingHandlerStub(request =>
        {
            forwardedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: "{}", encoding: Encoding.UTF8, mediaType: "application/json")
            });
        });

        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"qwen3-coder"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "http://127.0.0.1:1234/v1/chat/completions", actual: forwardedUri!.ToString());
    }

    [Fact]
    public async Task InvokeAsync_UnknownModel_NoModelsConfigured_Returns400_AndNeverCallsUpstream()
    {
        // The agentic fallback (below) has nothing to fall back to when the allowlist itself is empty, so
        // this is the one case where an unresolved model still 400s before ever reaching the upstream call.
        var interceptor = new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: ModelRouteResolverTestFactory.Empty());

        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for an unknown model."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"totally-unknown-model"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status400BadRequest, actual: context.Response.StatusCode);
        Assert.Equal(expected: "application/json", actual: context.Response.ContentType);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        var responseBody = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        Assert.Equal(expected: "invalid_request_error",
            actual: document.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Contains(expectedSubstring: "totally-unknown-model",
            actualString: document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    // docs/router/utility-model-routing.md's generalized fallback: outside single-model serving, an
    // unresolved model is accepted and forwarded to a real, allowlisted candidate - here the extension's
    // default totallyhot.spark.modelId ("agentic-router") lands on the only configured model, "gpt-5.4".
    [Fact]
    public async Task InvokeAsync_UnknownModel_ModelsConfigured_AgenticallyRoutesToConfiguredModel_AndCallsUpstream()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(async request =>
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(expected: "gpt-5.4-2026-01", actual: document.RootElement.GetProperty("model").GetString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: "forwarded", encoding: Encoding.UTF8, mediaType: "text/plain")
            };
        });
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"agentic-router"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_AddsConfiguredCustomHeader_WhenClientDidNotSendIt()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4", baseUrl: "https://example.com",
            headers: [new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01" }]);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(request =>
        {
            Assert.Equal(expected: "2023-06-01", actual: Assert.Single(request.Headers.GetValues("anthropic-version")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        });

        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = BuildForwardableContext();
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotClobberCustomHeader_WhenClientAlreadySentIt()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4", baseUrl: "https://example.com",
            headers: [new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01" }]);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(request =>
        {
            // The client's own value wins; the provider default is not added on top or in place of it.
            Assert.Equal(expected: "2099-01-01", actual: Assert.Single(request.Headers.GetValues("anthropic-version")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        });

        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = BuildForwardableContext();
        context.Request.Headers["anthropic-version"] = "2099-01-01";
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    private static DefaultHttpContext BuildForwardableContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task InvokeAsync_StripsHeadersNominatedByRequestConnectionHeader()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://example.com");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(request =>
        {
            Assert.False(request.Headers.Contains("X-Nominated"));
            Assert.True(request.Headers.Contains("X-Kept"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        });

        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        context.Request.Headers["Connection"] = "X-Nominated";
        context.Request.Headers["X-Nominated"] = "should-be-stripped";
        context.Request.Headers["X-Kept"] = "should-be-forwarded";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_StripsHeadersNominatedByResponseConnectionHeader()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://example.com");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            response.Headers.Add(name: "Connection", value: "X-Custom");
            response.Headers.Add(name: "X-Custom", value: "should-be-stripped");
            response.Headers.Add(name: "X-Kept", value: "should-be-forwarded");
            return Task.FromResult(response);
        });

        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.False(context.Response.Headers.ContainsKey("X-Custom"));
        Assert.False(context.Response.Headers.ContainsKey("Connection"));
        Assert.Equal(expected: "should-be-forwarded", actual: context.Response.Headers["X-Kept"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_WhenForwardingFailsAndNoFallbackConfigured_Returns502()
    {
        // A single-candidate route (no fallbacks) whose only upstream is unreachable now returns a clean
        // 502 envelope - the same upstream-outage response the Bedrock SDK path already produces - rather
        // than letting the HttpRequestException escape as an unhandled 500. (Previously this threw; the
        // failover cascade unified transport-outage handling on the exhausted-candidates 502 path.)
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4",
            baseUrl: "https://api.openai.com");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ => throw new HttpRequestException("upstream unavailable"));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/fail";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status502BadGateway, actual: context.Response.StatusCode);
    }

    // Verifies that GET /v1/models is answered locally from the configured ModelList, in OpenAI's model
    // list shape, and never reaches the upstream HTTP handler (there is no single upstream to forward a
    // multi-provider model list to).
    [Fact]
    public async Task InvokeAsync_GetModelsList_ReturnsConfiguredModels_AsOpenAiShapedList_WithoutCallingUpstream()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4-2026-01"),
            ("claude-opus-4.6", "anthropic", "claude-opus-4-6"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /v1/models."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/models";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "application/json", actual: context.Response.ContentType);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected: "list", actual: document.RootElement.GetProperty("object").GetString());
        var data = document.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(3, actual: data.Count);

        // The synthetic "let the router choose" entry leads the list - see the dedicated test below.
        Assert.Equal(expected: "totallyhot-arcrouter", actual: data[0].GetProperty("id").GetString());

        Assert.Equal(expected: "gpt-5.4", actual: data[1].GetProperty("id").GetString());
        Assert.Equal(expected: "model", actual: data[1].GetProperty("object").GetString());
        Assert.Equal(0, actual: data[1].GetProperty("created").GetInt64());
        Assert.Equal(expected: "openai", actual: data[1].GetProperty("owned_by").GetString());

        Assert.Equal(expected: "claude-opus-4.6", actual: data[2].GetProperty("id").GetString());
        Assert.Equal(expected: "anthropic", actual: data[2].GetProperty("owned_by").GetString());
    }

    // VS Code / Copilot attaches to this proxy as an OpenAI-compatible provider and discovers models here
    // rather than via Ollama's /api/tags, so the router entry has to be present in this shape too - it is
    // the only way a user of that client can ask the router to choose. Reported under its own owned_by,
    // since no configured provider owns it.
    [Fact]
    public async Task InvokeAsync_GetModelsList_ListsTheRouterEntryFirst()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4-2026-01"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /v1/models."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/models";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        var data = document.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(expected: "totallyhot-arcrouter", actual: data[0].GetProperty("id").GetString());
        Assert.Equal(expected: "totallyhot", actual: data[0].GetProperty("owned_by").GetString());
    }

    // Verifies that an empty ModelList still yields a valid, empty OpenAI-shaped response rather than an
    // error, so a freshly configured proxy with no routes yet doesn't break model discovery.
    [Fact]
    public async Task InvokeAsync_GetModelsList_EmptyModelList_ReturnsEmptyDataArray()
    {
        var interceptor = new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(),
            modelRouteResolver: ModelRouteResolverTestFactory.Empty());
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /v1/models."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/models";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        Assert.Empty(document.RootElement.GetProperty("data").EnumerateArray());
    }

    // Verifies the path match is case-insensitive, since client conventions for path casing vary.
    [Fact]
    public async Task InvokeAsync_GetModelsList_IsCaseInsensitiveOnPath()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /v1/models."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/V1/MODELS";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    // Verifies that a trailing slash on the path is tolerated, since some clients/proxies normalize
    // requests to include one (e.g. GET /v1/models/) and it should still be treated as model discovery.
    [Fact]
    public async Task InvokeAsync_GetModelsList_TrailingSlashIsTolerated()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /v1/models."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/models/";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    // Verifies that a non-GET request to the /v1/models path is not treated as a model-discovery request:
    // it still falls through to normal per-model routing, since the discovery short-circuit is GET-only.
    [Fact]
    public async Task InvokeAsync_PostToModelsPath_IsNotTreatedAsModelsListRequest_AndIsForwardedNormally()
    {
        var resolver = ModelRouteResolverTestFactory.Create(modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://api.openai.com");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(request =>
        {
            Assert.Equal(expected: HttpMethod.Post, actual: request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        });
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/models";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    // Verifies that GET /api/tags is answered locally from the configured ModelList, in Ollama's native
    // tag list shape, and never reaches the upstream HTTP handler - the same local-discovery treatment
    // /v1/models gets, so a client that adds this proxy as an "Ollama" provider (e.g. Visual Studio) can
    // discover the configured models instead of getting a 400 from the normal per-model routing path.
    [Fact]
    public async Task InvokeAsync_GetOllamaTags_ReturnsConfiguredModels_AsOllamaShapedList_WithoutCallingUpstream()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(
            ("gpt-5.4", "openai", "gpt-5.4-2026-01"),
            ("claude-opus-4.6", "anthropic", "claude-opus-4-6"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /api/tags."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/api/tags";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "application/json", actual: context.Response.ContentType);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        var models = document.RootElement.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(3, actual: models.Count);

        // The synthetic "let the router choose" entry leads the list - see the dedicated test below.
        Assert.Equal(expected: "totallyhot-arcrouter", actual: models[0].GetProperty("name").GetString());

        Assert.Equal(expected: "gpt-5.4", actual: models[1].GetProperty("name").GetString());
        Assert.Equal(expected: "gpt-5.4", actual: models[1].GetProperty("model").GetString());

        Assert.Equal(expected: "claude-opus-4.6", actual: models[2].GetProperty("name").GetString());
        Assert.Equal(expected: "claude-opus-4.6", actual: models[2].GetProperty("model").GetString());
    }

    // Visual Studio's Ollama model picker only lets the user choose a name this endpoint returned, so the
    // router entry must appear here for "let the router choose" to be selectable at all. It leads the list
    // so it reads as the default choice.
    [Fact]
    public async Task InvokeAsync_GetOllamaTags_ListsTheRouterEntryFirst()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4-2026-01"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /api/tags."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/tags";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        var models = document.RootElement.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(expected: "totallyhot-arcrouter", actual: models[0].GetProperty("name").GetString());
        Assert.Equal(expected: "totallyhot-arcrouter", actual: models[0].GetProperty("model").GetString());
    }

    // The step that would silently break the picker: Visual Studio POSTs /api/show for the model the user
    // selected before using it, and this endpoint 404s any name absent from the discovery list. Listing the
    // router entry in /api/tags without this would let it be selected and then immediately fail.
    [Fact]
    public async Task InvokeAsync_PostOllamaShow_RouterModel_Returns200()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4-2026-01"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /api/show."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/show";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"totallyhot-arcrouter"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    // Verifies that a trailing slash on the path is tolerated, mirroring /v1/models' tolerance.
    [Fact]
    public async Task InvokeAsync_GetOllamaTags_TrailingSlashIsTolerated()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /api/tags."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/tags/";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
    }

    // Verifies that POST /api/show is answered locally for a configured model - the follow-up request an
    // Ollama-shaped client (e.g. Visual Studio's AI model picker) makes per model after GET /api/tags - and
    // never reaches the upstream HTTP handler. Without this, the request falls through to the normal
    // per-model routing path and gets forwarded upstream as a malformed chat/completion request.
    [Fact]
    public async Task InvokeAsync_PostOllamaShow_KnownModel_ReturnsOllamaShapedDetails_WithoutCallingUpstream()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4-2026-01"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /api/show."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/show";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        Assert.Equal(expected: "application/json", actual: context.Response.ContentType);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        Assert.True(document.RootElement.TryGetProperty(propertyName: "template", value: out _));
        Assert.True(document.RootElement.TryGetProperty(propertyName: "details", value: out _));
    }

    // Verifies that POST /api/show for a model this proxy does not have configured answers 404 with an
    // Ollama-shaped error body, matching real Ollama's own behavior, instead of falling through to the
    // normal per-model routing path.
    [Fact]
    public async Task InvokeAsync_PostOllamaShow_UnknownModel_Returns404_WithoutCallingUpstream()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelList(("gpt-5.4", "openai", "gpt-5.4-2026-01"));
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            throw new InvalidOperationException("Upstream should never be called for /api/show."));
        var middleware = new ProxyMiddleware(logger: Mock.Of<ILogger<ProxyMiddleware>>(), interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/show";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"deepseek-coder-6.7b-instruct"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status404NotFound, actual: context.Response.StatusCode);
    }

    // Covers the telemetry integration added to ProxyMiddleware: session resolution from a request
    // header, turn tracking, provider-aware usage extraction from the (non-streaming) upstream
    // response body, and publishing the resulting event - all layered on top of forwarding behavior
    // that is otherwise unchanged from the tests above.
    [Fact]
    public async Task InvokeAsync_SuccessfulNonStreamingOpenAiResponse_PublishesRoutingTelemetryEvent()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7,"total_tokens":49}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        }));

        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        context.Request.Headers["x-claude-code-session-id"] = "sess-42";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        telemetryPublisherMock.Verify(
            expression: p => p.PublishAsync(
                It.Is<RoutingTelemetryEvent>(e =>
                    e.SessionId == "sess-42" &&
                    e.TurnNumber == 1 &&
                    !e.IsSessionSynthesized &&
                    e.RequestedModel == "gpt-5.4" &&
                    e.ResolvedModel == "gpt-5.4-2026-01" &&
                    e.Provider == "openai" &&
                    e.PromptTokens == 42 &&
                    e.CompletionTokens == 7 &&
                    !e.IsStreaming &&
                    e.StatusCode == 200),
                It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    // Every completed request - not just ones with a known cost - must reach the spend tracker so
    // its running request count stays accurate.
    [Fact]
    public async Task InvokeAsync_SuccessfulNonStreamingOpenAiResponse_RecordsSpendViaSpendTracker()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7,"total_tokens":49}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        }));

        var spendTrackerMock = new Mock<ISpendTracker>();
        spendTrackerMock
            .Setup(t => t.RecordAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<decimal?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpendSummary(1, 42, 7, 0m));
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                SpendTracker = spendTrackerMock.Object
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        // "gpt-5.4" routes to a paid provider and no price lookup is wired into this middleware (the
        // default), so cost is unknown (null) even though usage was extracted - the spend tracker still
        // gets the real token counts. Unknown is the honest answer here; it must not be reported as a zero.
        // The catalog-priced counterpart is InvokeAsync_PaidProviderWithCatalogPrice_RecordsEstimatedCostFromCatalog.
        spendTrackerMock.Verify(
            expression: t => t.RecordAsync("gpt-5.4", 42, 7, null, It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    // The counterpart to the test above, and the distinction the whole IsFree flag exists to draw: a
    // free provider's cost is a known 0, not an unknown null. A local runtime genuinely costs nothing,
    // which is a fact about the deployment rather than an estimate - so unlike a paid model with no
    // catalog price, it does have a cost to report.
    [Fact]
    public async Task InvokeAsync_FreeProvider_RecordsZeroCostRatherThanUnknown()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "llama3",
            providerModelId: "llama3",
            baseUrl: "http://localhost:11434/v1",
            providerName: "ollama",
            isFree: true);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7,"total_tokens":49}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        }));

        var spendTrackerMock = new Mock<ISpendTracker>();
        spendTrackerMock
            .Setup(t => t.RecordAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<decimal?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpendSummary(1, 42, 7, 0m));
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                SpendTracker = spendTrackerMock.Object
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"llama3"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        spendTrackerMock.Verify(
            expression: t => t.RecordAsync("llama3", 42, 7, 0m, It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    // The cost half of the pillar, now that the price catalog is wired into the request path: a paid model
    // with a fresh catalog price gets a real per-request USD cost (tokens x catalog rate), not null. The
    // lookup is keyed by the client-facing ModelName (route.ModelName, "gpt-5.4") - the identity D3 alias
    // resolution stores catalog prices under - asserted via the exact ModelKey the fake is set up for.
    [Fact]
    public async Task InvokeAsync_PaidProviderWithCatalogPrice_RecordsEstimatedCostFromCatalog()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7,"total_tokens":49}}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        }));

        // $2 / M input, $6 / M output. Expected cost for 42 prompt + 7 completion tokens:
        // 42/1e6*2 + 7/1e6*6 = 0.000084 + 0.000042 = 0.000126.
        var priceLookup = new Mock<IModelPriceLookup>();
        priceLookup
            .Setup(l => l.TryGetPrice(new ModelKey(ModelName: "gpt-5.4", Provider: "openai")))
            .Returns(new ModelPrice(2m, 6m));

        var spendTrackerMock = new Mock<ISpendTracker>();
        spendTrackerMock
            .Setup(t => t.RecordAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<decimal?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpendSummary(1, 42, 7, 0.000126m));
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                SpendTracker = spendTrackerMock.Object,
                PriceLookup = priceLookup.Object
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        spendTrackerMock.Verify(
            expression: t => t.RecordAsync("gpt-5.4", 42, 7, 0.000126m, It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    // §5.6: the published telemetry event's CostConfidence must match how the cost was actually arrived
    // at, across every branch PublishTelemetryAsync computes it from.
    [Fact]
    public async Task InvokeAsync_FreeProvider_ReportsExactCostConfidence()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "llama3", providerModelId: "llama3", baseUrl: "http://localhost:11434/v1",
            providerName: "ollama", isFree: true);
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"c1","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""",
                encoding: Encoding.UTF8, mediaType: "application/json")
        }));
        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object
            }
        );

        var context = NewJsonPostContext("""{"model":"llama3"}""");
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        telemetryPublisherMock.Verify(
            expression: p => p.PublishAsync(It.Is<RoutingTelemetryEvent>(e => e.CostConfidence == CostConfidence.Exact),
                It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_PaidProviderWithFullCatalogPrice_ReportsCatalogCostConfidence()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01", baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"c1","choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7,"total_tokens":49}}""",
                encoding: Encoding.UTF8, mediaType: "application/json")
        }));
        var priceLookup = new Mock<IModelPriceLookup>();
        priceLookup.Setup(l => l.TryGetPrice(new ModelKey("gpt-5.4", "openai"))).Returns(new ModelPrice(2m, 6m));
        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object,
                PriceLookup = priceLookup.Object
            }
        );

        var context = NewJsonPostContext("""{"model":"gpt-5.4"}""");
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        telemetryPublisherMock.Verify(
            expression: p =>
                p.PublishAsync(It.Is<RoutingTelemetryEvent>(e => e.CostConfidence == CostConfidence.Catalog),
                    It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_PaidProviderWithUnpublishedCacheRate_ReportsCatalogApproximateCostConfidence()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01", baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"c1","choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7,"total_tokens":49,"prompt_tokens_details":{"cached_tokens":10}}}""",
                encoding: Encoding.UTF8, mediaType: "application/json")
        }));
        // No cache rates published, but the response carries cached tokens - EstimateCost falls back to
        // the standard input rate for them, which must be reported as an approximate, not exact, cost.
        var priceLookup = new Mock<IModelPriceLookup>();
        priceLookup.Setup(l => l.TryGetPrice(new ModelKey("gpt-5.4", "openai"))).Returns(new ModelPrice(2m, 6m));
        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object,
                PriceLookup = priceLookup.Object
            }
        );

        var context = NewJsonPostContext("""{"model":"gpt-5.4"}""");
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        telemetryPublisherMock.Verify(
            expression: p =>
                p.PublishAsync(It.Is<RoutingTelemetryEvent>(e => e.CostConfidence == CostConfidence.CatalogApproximate),
                    It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_PaidProviderWithNoCatalogPrice_ReportsUnknownCostConfidence()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01", baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"c1","choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7,"total_tokens":49}}""",
                encoding: Encoding.UTF8, mediaType: "application/json")
        }));
        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object
            }
        );

        var context = NewJsonPostContext("""{"model":"gpt-5.4"}""");
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        telemetryPublisherMock.Verify(
            expression: p =>
                p.PublishAsync(It.Is<RoutingTelemetryEvent>(e => e.CostConfidence == CostConfidence.Unknown),
                    It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_NoUsageExtracted_ReportsNoUsageCostConfidence()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01", baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"c1","choices":[]}""", encoding: Encoding.UTF8,
                mediaType: "application/json")
        }));
        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object
            }
        );

        var context = NewJsonPostContext("""{"model":"gpt-5.4"}""");
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        telemetryPublisherMock.Verify(
            expression: p =>
                p.PublishAsync(It.Is<RoutingTelemetryEvent>(e => e.CostConfidence == CostConfidence.NoUsage),
                    It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    /// <summary>Builds a minimal POST context with the given JSON request body, for the CostConfidence tests above.</summary>
    private static DefaultHttpContext NewJsonPostContext(string jsonBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes(jsonBody);
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    // A client-supplied session id is logged (LogDebug "Resolved session {SessionId}...") but is
    // otherwise attacker-controlled, arbitrary text - a value containing CR/LF must not be able to
    // forge extra-looking lines in a text log sink (CodeQL: "Log entries created from user input").
    // The published telemetry event, a structured object rather than rendered text, is unaffected -
    // only the log message rendering is sanitized.
    [Fact]
    public async Task InvokeAsync_SessionIdContainsNewlines_SanitizesLogMessageButNotThePublishedEvent()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));

        var loggerMock = new Mock<ILogger<ProxyMiddleware>>();
        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: loggerMock.Object,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object
            }
        );

        const string maliciousSessionId = "sess-1\r\nINFO: FAKE INJECTED LOG LINE";

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        context.Request.Headers["x-claude-code-session-id"] = maliciousSessionId;
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        telemetryPublisherMock.Verify(
            expression: p => p.PublishAsync(
                It.Is<RoutingTelemetryEvent>(e => e.SessionId == maliciousSessionId),
                It.IsAny<CancellationToken>()),
            times: Times.Once);

        loggerMock.Verify(
            expression: logger => logger.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("Resolved session", StringComparison.Ordinal) &&
                    !state.ToString()!.Contains('\r') &&
                    !state.ToString()!.Contains('\n')),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times: Times.Once);
    }

    // The newest user message and the assistant's reply text must both reach the published event,
    // truncated via TextTruncator - not the raw resent message history.
    [Fact]
    public async Task InvokeAsync_NonStreamingResponseWithMessages_PublishesRequestAndResponseSummaries()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"The capital of France is Paris."}}]}""",
                encoding: Encoding.UTF8,
                mediaType: "application/json")
        }));

        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        context.Request.Headers["x-claude-code-session-id"] = "sess-text";
        var requestBody = Encoding.UTF8.GetBytes(
            """{"model":"gpt-5.4","messages":[{"role":"system","content":"You are helpful."},{"role":"user","content":"What is the capital of France?"}]}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        telemetryPublisherMock.Verify(
            expression: p => p.PublishAsync(
                It.Is<RoutingTelemetryEvent>(e =>
                    e.RequestSummary == "What is the capital of France?" &&
                    e.ResponseSummary == "The capital of France is Paris."),
                It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    // A second request in the same session must be turn 2, not a fresh turn 1 - confirms the turn
    // tracker is a shared, stateful dependency across calls on the same middleware instance, not
    // reset per-request.
    [Fact]
    public async Task InvokeAsync_SecondRequestInSameSession_IsTurnTwo()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));

        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object
            }
        );

        async Task SendOnceAsync()
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("127.0.0.1:5001");
            context.Request.Path = "/chat";
            context.Request.Headers["x-claude-code-session-id"] = "sess-repeat";
            var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
            context.Request.Body = new MemoryStream(requestBody);
            context.Request.ContentLength = requestBody.Length;
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);
        }

        await SendOnceAsync();
        await SendOnceAsync();

        telemetryPublisherMock.Verify(
            expression: p =>
                p.PublishAsync(It.Is<RoutingTelemetryEvent>(e => e.TurnNumber == 1), It.IsAny<CancellationToken>()),
            times: Times.Once);
        telemetryPublisherMock.Verify(
            expression: p =>
                p.PublishAsync(It.Is<RoutingTelemetryEvent>(e => e.TurnNumber == 2), It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    // No session id anywhere in the request: the middleware must still publish (a synthesized,
    // single-turn "session"), not silently drop telemetry for sessionless requests.
    [Fact]
    public async Task InvokeAsync_NoResolvableSessionId_PublishesWithSynthesizedSingleTurnSession()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));

        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        telemetryPublisherMock.Verify(
            expression: p => p.PublishAsync(
                It.Is<RoutingTelemetryEvent>(e =>
                    e.IsSessionSynthesized && e.TurnNumber == 1 && !string.IsNullOrEmpty(e.SessionId)),
                It.IsAny<CancellationToken>()),
            times: Times.Once);
    }

    // A publisher failure must never surface as a proxy error: the client-facing response is
    // unaffected regardless of what telemetry publishing does.
    [Fact]
    public async Task InvokeAsync_TelemetryPublisherThrows_ClientResponseIsStillCorrect()
    {
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4",
            providerModelId: "gpt-5.4-2026-01",
            baseUrl: "https://example.com",
            providerName: "openai");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            { Content = new StringContent("forwarded") }));

        var telemetryPublisherMock = new Mock<ITelemetryPublisher>();
        telemetryPublisherMock
            .Setup(p => p.PublishAsync(It.IsAny<RoutingTelemetryEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var middleware = new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisherMock.Object
            }
        );

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"gpt-5.4"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status202Accepted, actual: context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        Assert.Equal(expected: "forwarded", actual: await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task
        InvokeAsync_UpstreamReadAbortedMidStream_FailsOpen_WritesWhatArrivedAndLogsWarning_WithoutThrowing()
    {
        // Reproduces the "aborted because of either a thread exit or an application request" IOException/
        // SocketException a live upstream read can throw mid-stream (e.g. a dotnet watch hot reload or
        // debugger stop tearing down the connection). CopyAndCaptureAsync (the no-translator passthrough
        // path a provider like ollama takes) must fail open - write whatever arrived before the abort, log
        // a warning, and return normally - rather than let the exception propagate out of InvokeAsync.
        var resolver = ModelRouteResolverTestFactory.Create(
            modelName: "llama3",
            providerModelId: "llama3",
            baseUrl: "http://localhost:11434/v1",
            providerName: "ollama");
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);

        var handler = new DelegatingHandlerStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new AbortsAfterFirstReadStream(Encoding.UTF8.GetBytes("partial-chunk")))
        }));

        var loggerMock = new Mock<ILogger<ProxyMiddleware>>();
        var middleware = new ProxyMiddleware(logger: loggerMock.Object, interceptor: interceptor,
            httpClient: new HttpClient(handler));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/chat";
        var requestBody = Encoding.UTF8.GetBytes("""{"model":"llama3"}""");
        context.Request.Body = new MemoryStream(requestBody);
        context.Request.ContentLength = requestBody.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        Assert.Equal(expected: "partial-chunk",
            actual: await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        loggerMock.Verify(
            expression: l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(e => e is IOException),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times: Times.Once);
    }

    /// <summary>
    /// Yields one chunk of bytes on its first read, then throws the exact IOException(SocketException) shape a
    /// mid-stream connection abort produces on every subsequent read.
    /// </summary>
    private sealed class AbortsAfterFirstReadStream : Stream
    {
        private readonly byte[] _firstChunk;
        private bool _served;

        public AbortsAfterFirstReadStream(byte[] firstChunk)
        {
            _firstChunk = firstChunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_served)
            {
                _served = true;
                _firstChunk.CopyTo(buffer);
                return ValueTask.FromResult(_firstChunk.Length);
            }

            throw new IOException(
                message:
                "Unable to read data from the transport connection: The I/O operation has been aborted because of either a thread exit or an application request.",
                innerException: new SocketException((int)SocketError.OperationAborted));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}