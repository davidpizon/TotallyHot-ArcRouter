using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Cache;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Proves the semantic cache, wired through <see cref="ProxyMiddleware"/>, serves a similar request
/// without a second upstream call, and leaves forwarding unchanged when it is disabled or the request
/// is unsafe to reuse.
/// </summary>
public class ProxyMiddlewareSemanticCacheTests
{
    private const string AnswerJson = """{"choices":[{"message":{"role":"assistant","content":"four"}}]}""";

    /// <summary>
    /// A repeated, equivalent question is answered from the cache. The provider is called once.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_EquivalentRequest_IsServedFromTheCache()
    {
        var calls = 0;
        var logger = new CaptureLogger();
        var cache = NewCache(enabled: true);
        using var middleware = CreateMiddleware(cache: cache, logger: logger, onUpstream: () => calls++,
            embed: _ => [1f, 0f]);

        var first = await SendAsync(middleware: middleware,
            json: """{"model":"gpt-5.4","temperature":0.2,"messages":[{"role":"user","content":"What is two plus two?"}]}""");
        var second = await SendAsync(middleware: middleware,
            json: """{"model":"gpt-5.4","temperature":0.2,"messages":[{"role":"user","content":"What's 2+2?"}]}""");

        Assert.Equal(1, actual: calls);
        Assert.Equal(expected: StatusCodes.Status200OK, actual: first.Response.StatusCode);
        Assert.Equal(expected: "miss", actual: Header(first));
        Assert.Equal(expected: "hit", actual: Header(second));
        Assert.Equal(expected: AnswerJson, actual: await ReadBodyAsync(second));
        Assert.Contains(collection: logger.Messages,
            filter: message => message.Contains(value: "Local semantic cache hit", comparisonType: StringComparison.Ordinal)
                               && message.Contains(value: "separate from provider prompt caching",
                                   comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: logger.Messages,
            filter: message => message.Contains(value: "Local semantic cache miss", comparisonType: StringComparison.Ordinal));
    }

    /// <summary>A question the embedding says is different still calls the provider.</summary>
    [Fact]
    public async Task InvokeAsync_BelowThreshold_CallsTheProviderAgain()
    {
        var calls = 0;
        var logger = new CaptureLogger();
        var cache = NewCache(enabled: true);
        using var middleware = CreateMiddleware(cache: cache, logger: logger, onUpstream: () => calls++,
            embed: text => text.Contains(value: "weather", comparisonType: StringComparison.OrdinalIgnoreCase)
                ? [0f, 1f]
                : [1f, 0f]);

        await SendAsync(middleware: middleware,
            json: """{"model":"gpt-5.4","temperature":0.2,"messages":[{"role":"user","content":"What is two plus two?"}]}""");
        var second = await SendAsync(middleware: middleware,
            json: """{"model":"gpt-5.4","temperature":0.2,"messages":[{"role":"user","content":"What is the weather?"}]}""");

        Assert.Equal(2, actual: calls);
        Assert.Equal(expected: "miss", actual: Header(second));
        Assert.Contains(collection: logger.Messages,
            filter: message => message.Contains(value: "below-threshold", comparisonType: StringComparison.Ordinal));
    }

    /// <summary>Disabled means no header, no store, and both requests reach the provider.</summary>
    [Fact]
    public async Task InvokeAsync_Disabled_DoesNotLookUpOrStore()
    {
        var calls = 0;
        var logger = new CaptureLogger();
        var cache = NewCache(enabled: false);
        using var middleware = CreateMiddleware(cache: cache, logger: logger, onUpstream: () => calls++,
            embed: _ => [1f, 0f]);
        var json = """{"model":"gpt-5.4","temperature":0.2,"messages":[{"role":"user","content":"What is two plus two?"}]}""";

        var first = await SendAsync(middleware: middleware, json: json);
        var second = await SendAsync(middleware: middleware, json: json);

        Assert.Equal(2, actual: calls);
        Assert.Equal(0, actual: cache.Count);
        Assert.Equal(0, actual: cache.ComparisonCount);
        Assert.Null(Header(first));
        Assert.Null(Header(second));
        Assert.Equal(expected: AnswerJson, actual: await ReadBodyAsync(second));
        Assert.DoesNotContain(collection: logger.Messages,
            filter: message => message.Contains(value: "semantic cache", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tool-bearing requests, streams, errors, and tool-call answers are never reused.</summary>
    [Fact]
    public async Task InvokeAsync_UnsafeResponses_AreNeverReused()
    {
        await AssertNeverReused(
            json: """{"model":"gpt-5.4","tools":[{"type":"function","function":{"name":"add"}}],"messages":[{"role":"user","content":"What is two plus two?"}]}""",
            status: HttpStatusCode.OK,
            responseJson: AnswerJson);
        await AssertNeverReused(
            json: """{"model":"gpt-5.4","stream":true,"messages":[{"role":"user","content":"What is two plus two?"}]}""",
            status: HttpStatusCode.OK,
            responseJson: AnswerJson);
        await AssertNeverReused(
            json: """{"model":"gpt-5.4","messages":[{"role":"user","content":"What is two plus two?"}]}""",
            status: HttpStatusCode.BadGateway,
            responseJson: """{"error":{"message":"down"}}""");
        await AssertNeverReused(
            json: """{"model":"gpt-5.4","messages":[{"role":"user","content":"What is two plus two?"}]}""",
            status: HttpStatusCode.OK,
            responseJson: """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"add","arguments":"{}"}}]}}]}""");
    }

    /// <summary>An alias of the same upstream model with the same settings reuses the saved answer.</summary>
    [Fact]
    public async Task InvokeAsync_AliasWithEquivalentSettings_IsServedFromTheCache()
    {
        var calls = 0;
        var resolver = ModelRouteResolverTestFactory.CreateWithModels(
            ("fast", "prov", "gpt-real", "https://stub.test"),
            ("fast-alias", "prov", "gpt-real", "https://stub.test"));
        var cache = NewCache(enabled: true);
        using var middleware = CreateMiddleware(cache: cache, logger: new CaptureLogger(), onUpstream: () => calls++,
            embed: _ => [1f, 0f], resolver: resolver);

        await SendAsync(middleware: middleware,
            json: """{"model":"fast","temperature":0,"messages":[{"role":"user","content":"What is two plus two?"}]}""");
        var second = await SendAsync(middleware: middleware,
            json: """{"model":"fast-alias","temperature":0,"messages":[{"role":"user","content":"What's two plus two?"}]}""");

        Assert.Equal(1, actual: calls);
        Assert.Equal(expected: "hit", actual: Header(second));
    }

    /// <summary>After the TTL, the same question calls the provider again.</summary>
    [Fact]
    public async Task InvokeAsync_ExpiredEntry_CallsTheProviderAgain()
    {
        var calls = 0;
        var clock = new ManualTimeProvider { UtcNow = DateTimeOffset.Parse("2026-09-28T00:00:00Z") };
        var cache = NewCache(enabled: true, timeToLive: TimeSpan.FromMinutes(5), time: clock);
        using var middleware = CreateMiddleware(cache: cache, logger: new CaptureLogger(), onUpstream: () => calls++,
            embed: _ => [1f, 0f]);
        var json = """{"model":"gpt-5.4","messages":[{"role":"user","content":"What is two plus two?"}]}""";

        await SendAsync(middleware: middleware, json: json);
        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        var second = await SendAsync(middleware: middleware, json: json);

        Assert.Equal(2, actual: calls);
        Assert.Equal(expected: "miss", actual: Header(second));
    }

    private static async Task AssertNeverReused(string json, HttpStatusCode status, string responseJson)
    {
        var calls = 0;
        var cache = NewCache(enabled: true);
        using var middleware = CreateMiddleware(cache: cache, logger: new CaptureLogger(), onUpstream: () => calls++,
            embed: _ => [1f, 0f], status: status, responseJson: responseJson);

        await SendAsync(middleware: middleware, json: json);
        await SendAsync(middleware: middleware, json: json);

        Assert.Equal(2, actual: calls);
        Assert.Equal(0, actual: cache.Count);
    }

    private static SemanticResponseCache NewCache(bool enabled, TimeSpan? timeToLive = null, TimeProvider? time = null)
    {
        var options = new SemanticCacheOptions
        {
            Enabled = enabled,
            SimilarityThreshold = 0.92,
            TimeToLive = timeToLive ?? TimeSpan.FromHours(1),
            MaxEntries = 32
        };
        return new SemanticResponseCache(options: new FixedMonitor(options), embeddingClient: new FixedEmbeddingClient(),
            timeProvider: time);
    }

    private static ProxyMiddleware CreateMiddleware(
        SemanticResponseCache cache,
        CaptureLogger logger,
        Action onUpstream,
        Func<string, float[]> embed,
        IModelRouteResolver? resolver = null,
        HttpStatusCode status = HttpStatusCode.OK,
        string? responseJson = null)
    {
        resolver ??= ModelRouteResolverTestFactory.Create(
            modelName: "gpt-5.4", providerModelId: "gpt-5.4-2026-01", baseUrl: "https://example.com");
        var warmup = new EmbeddingWarmupState();
        warmup.MarkWarm();
        var embeddingClient = new MapEmbeddingClient(embed);
        var interceptor = new RequestInterceptor(
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver: resolver,
            embeddingClient: embeddingClient,
            embeddingWarmupState: warmup);
        var handler = new DelegatingHandlerStub(_ =>
        {
            onUpstream();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content: responseJson ?? AnswerJson, encoding: Encoding.UTF8,
                    mediaType: "application/json")
            });
        });

        // The cache compares vectors; identity must match the client that produced them. The map client
        // reports the unknown identity, and so does a cache constructed without a named client. Rebind
        // by passing the same default identity: FixedEmbeddingClient uses the interface default.
        return new ProxyMiddleware(
            logger: logger,
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies { SemanticResponseCache = cache });
    }

    private static async Task<DefaultHttpContext> SendAsync(ProxyMiddleware middleware, string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1:5001");
        context.Request.Path = "/v1/chat/completions";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);
        return context;
    }

    private static string? Header(HttpContext context)
    {
        return context.Response.Headers[SemanticCacheCoordinator.HeaderName].ToString() is { Length: > 0 } value
            ? value
            : null;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FixedMonitor(SemanticCacheOptions options) : IOptionsMonitor<SemanticCacheOptions>
    {
        public SemanticCacheOptions CurrentValue => options;

        public SemanticCacheOptions Get(string? name)
        {
            return options;
        }

        public IDisposable OnChange(Action<SemanticCacheOptions, string?> listener)
        {
            return new Noop();
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class FixedEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class MapEmbeddingClient(Func<string, float[]> embed) : IEmbeddingClient
    {
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EmbeddingResult(Vector: embed(text), TokenCount: 1));
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

    private sealed class CaptureLogger : ILogger<ProxyMiddleware>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }
    }
}
