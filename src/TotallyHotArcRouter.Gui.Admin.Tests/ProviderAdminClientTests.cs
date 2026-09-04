using System.Net;
using System.Text;

namespace TotallyHot.ArcRouter.Gui.Admin.Tests;

/// <summary>
/// Unit coverage for <see cref="ProviderAdminClient"/>: request URLs/bodies/headers and response
/// (de)serialization against a stubbed transport, plus error-envelope handling. Runs cross-platform in
/// CI (the MAUI glue that wraps this client cannot).
/// </summary>
public sealed class ProviderAdminClientTests
{
    private const string ProvidersJson = """
                                         {
                                           "providers": [
                                             {
                                               "key": "openai",
                                               "baseUrl": "https://api.openai.com",
                                               "authHeaderName": "Authorization",
                                               "models": [ { "modelName": "gpt-5.4", "providerModelId": "gpt-5.4" } ],
                                               "headers": [ { "name": "anthropic-version", "value": "2023-06-01", "valueEnvVar": null } ],
                                               "dollarCap": 500.0,
                                               "tokenCap": 1000000,
                                               "dollarSpent": 12.5,
                                               "tokensUsed": 500
                                             }
                                           ]
                                         }
                                         """;

    private static ProviderAdminClient CreateClient(HttpMessageHandler handler, string? token = null)
    {
        return new ProviderAdminClient(
            httpClient: new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5001/") },
            adminToken: token);
    }

    [Fact]
    public async Task GetProvidersAsync_DeserializesProvidersAndModels()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        var providers = await client.GetProvidersAsync(TestContext.Current.CancellationToken);

        var provider = Assert.Single(providers);
        Assert.Equal(expected: "openai", actual: provider.Key);
        Assert.Equal(expected: "gpt-5.4", actual: Assert.Single(provider.Models).ModelName);
        Assert.Equal(expected: HttpMethod.Get, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers",
            actual: handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task UpsertProviderAsync_PutsToKeyedUrl_WithJsonBody()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.UpsertProviderAsync(
            key: "ollama",
            body: new ProviderWriteRequest(BaseUrl: "http://localhost:11434/v1", AuthHeaderName: "Authorization"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Put, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/ollama",
            actual: handler.LastRequest.RequestUri!.ToString());
        Assert.Contains(expectedSubstring: "\"baseUrl\":\"http://localhost:11434/v1\"", actualString: handler.LastBody,
            comparisonType: StringComparison.Ordinal);
    }

    // ProviderWriteRequest is duplicated on the proxy side (Proxy.Management) rather than shared, so the
    // two can drift silently. Pin the wire name the proxy binds against.
    [Fact]
    public async Task UpsertProviderAsync_SerializesIsFree_OnTheWire()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.UpsertProviderAsync(
            key: "ollama",
            body: new ProviderWriteRequest(BaseUrl: "http://localhost:11434/v1", AuthHeaderName: "Authorization",
                IsFree: true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expectedSubstring: "\"isFree\":true", actualString: handler.LastBody,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProvidersAsync_DeserializesBudgetCapsAndSpend()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        var provider = Assert.Single(await client.GetProvidersAsync(TestContext.Current.CancellationToken));

        Assert.Equal(500.0m, actual: provider.DollarCap);
        Assert.Equal(1_000_000L, actual: provider.TokenCap);
        Assert.Equal(12.5m, actual: provider.DollarSpent);
        Assert.Equal(500L, actual: provider.TokensUsed);
    }

    [Fact]
    public async Task SetBudgetAsync_PutsToBudgetUrl_WithCapsInBody()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.SetBudgetAsync(
            key: "openai",
            body: new ProviderBudgetWriteRequest(250m, 2_000_000L),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Put, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/openai/budget",
            actual: handler.LastRequest.RequestUri!.ToString());
        Assert.Contains(expectedSubstring: "\"dollarCap\":250", actualString: handler.LastBody,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "\"tokenCap\":2000000", actualString: handler.LastBody,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetBudgetAsync_NullCaps_SerializeAsNull()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.SetBudgetAsync(
            key: "openai",
            body: new ProviderBudgetWriteRequest(null, null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expectedSubstring: "\"dollarCap\":null", actualString: handler.LastBody,
            comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "\"tokenCap\":null", actualString: handler.LastBody,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetEnabledAsync_PutsToEnabledUrl_WithStateInBody()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.SetEnabledAsync(key: "openai", body: new ProviderEnabledWriteRequest(false),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Put, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/openai/enabled",
            actual: handler.LastRequest.RequestUri!.ToString());
        Assert.Contains(expectedSubstring: "\"enabled\":false", actualString: handler.LastBody,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProvidersAsync_MissingEnabled_DefaultsToOn()
    {
        // ProvidersJson predates the flag, standing in for a proxy that hasn't been updated yet: a provider
        // must never read as stopped just because the field is absent.
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        var provider = Assert.Single(await client.GetProvidersAsync(TestContext.Current.CancellationToken));

        Assert.True(provider.Enabled);
    }

    [Fact]
    public async Task UpsertModelAsync_PutsToNestedModelUrl()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.UpsertModelAsync(key: "ollama", modelName: "llama3", body: new ModelWriteRequest("llama3"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Put, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/ollama/models/llama3",
            actual: handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task RemoveProviderAsync_DeletesKeyedUrl()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.RemoveProviderAsync(key: "openai", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Delete, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/openai",
            actual: handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task DiscoverModelsAsync_DeserializesResult()
    {
        const string discoverJson = """{ "supported": true, "models": [ "gpt-5.4", "gpt-4o" ], "error": null }""";
        var handler = new StubHandler(_ => Json(discoverJson));
        var client = CreateClient(handler);

        var result =
            await client.DiscoverModelsAsync(key: "openai", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Supported);
        Assert.Equal(expected: ["gpt-5.4", "gpt-4o"], actual: result.Models);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/openai/discover-models",
            actual: handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ScanCapabilitiesAsync_DeserializesResult()
    {
        const string scanJson = """
                                {
                                  "providerKey": "lmstudio",
                                  "openAiCompatible": true,
                                  "lmStudioNative": true,
                                  "ollamaNative": false,
                                  "anthropicCompatible": false,
                                  "scannedAtUtc": "2026-07-31T00:00:00Z",
                                  "scanError": null
                                }
                                """;
        var handler = new StubHandler(_ => Json(scanJson));
        var client = CreateClient(handler);

        var result = await client.ScanCapabilitiesAsync(key: "lmstudio",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.OpenAiCompatible);
        Assert.True(result.LmStudioNative);
        Assert.False(result.OllamaNative);
        Assert.Equal(expected: HttpMethod.Post, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/lmstudio/scan-capabilities",
            actual: handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task RefreshFromEndpointAsync_PostsToTheRefreshRoute_AndDeserializesTheProviderList()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        var providers =
            await client.RefreshFromEndpointAsync(key: "openai",
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(providers);
        Assert.Equal(expected: HttpMethod.Post, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/openai/refresh-from-endpoint",
            actual: handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task SetModelEnabledAsync_PutsToTheModelEnabledRoute_WithTheRequestedState()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.SetModelEnabledAsync(key: "openai", modelName: "gpt-5.4",
            body: new ModelEnabledWriteRequest(false), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Put, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/openai/models/gpt-5.4/enabled",
            actual: handler.LastRequest.RequestUri!.ToString());
        Assert.Contains(expectedSubstring: "\"enabled\":false", actualString: handler.LastBody,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetModelToolDialectAsync_PutsToTheToolDialectRoute()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.SetModelToolDialectAsync(
            key: "openai", modelName: "gpt-5.4", body: new ModelToolDialectWriteRequest("constrained"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Put, actual: handler.LastRequest!.Method);
        Assert.Equal(expected: "http://localhost:5001/admin/providers/openai/models/gpt-5.4/tool-dialect",
            actual: handler.LastRequest.RequestUri!.ToString());
        Assert.Contains(expectedSubstring: "\"dialect\":\"constrained\"", actualString: handler.LastBody,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetModelToolDialectAsync_WithANullDialect_SendsTheClearingBody()
    {
        // Clearing the pin is the undo, so it must reach the server as an explicit null rather than being
        // dropped from the payload - the route reads a missing dialect the same way, but only by accident.
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.SetModelToolDialectAsync(
            key: "openai", modelName: "gpt-5.4", body: new ModelToolDialectWriteRequest(null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expectedSubstring: "\"dialect\":null", actualString: handler.LastBody,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetProvidersAsync_DeserializesDialectAndEndpointCapabilities()
    {
        const string json = """
                            {
                              "providers": [
                                {
                                  "key": "lmstudio",
                                  "baseUrl": "http://localhost:1234/v1",
                                  "authHeaderName": "Authorization",
                                  "models": [ { "modelName": "qwen2.5-coder", "providerModelId": "qwen2.5-coder", "dialect": "hermes", "confidence": "Observed", "enabled": false, "presentUpstream": false } ],
                                  "headers": [],
                                  "endpointCapabilities": {
                                    "providerKey": "lmstudio",
                                    "openAiCompatible": true,
                                    "lmStudioNative": true,
                                    "ollamaNative": false,
                                    "anthropicCompatible": false,
                                    "scannedAtUtc": "2026-07-31T00:00:00Z",
                                    "scanError": null
                                  }
                                }
                              ]
                            }
                            """;
        var handler = new StubHandler(_ => Json(json));
        var client = CreateClient(handler);

        var provider = Assert.Single(await client.GetProvidersAsync(TestContext.Current.CancellationToken));

        var model = Assert.Single(provider.Models);
        Assert.Equal(expected: "hermes", actual: model.Dialect);
        Assert.Equal(expected: "Observed", actual: model.Confidence);
        Assert.False(model.Enabled);
        Assert.False(model.PresentUpstream);
    }

    [Fact]
    public async Task GetProvidersAsync_WithNoDialectOrCapabilityFields_LeavesThemNull()
    {
        // A never-scanned provider's response omits the new fields entirely (they are optional server
        // side); the client must deserialize that as null rather than fail.
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        var provider = Assert.Single(await client.GetProvidersAsync(TestContext.Current.CancellationToken));

        var model = Assert.Single(provider.Models);
        Assert.Null(model.Dialect);
        // Enabled/PresentUpstream are also omitted from this fixture - both default true, same
        // back-compat reasoning as ProviderAdminView.Enabled.
        Assert.True(model.Enabled);
        Assert.True(model.PresentUpstream);
    }

    [Fact]
    public async Task ErrorResponse_ThrowsWithServerMessage()
    {
        const string errorJson =
            """{ "error": { "message": "ModelList entry 'x' references unknown provider 'y'.", "type": "invalid_request_error", "code": "400" } }""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(content: errorJson, encoding: Encoding.UTF8, mediaType: "application/json")
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.RemoveProviderAsync(key: "openai", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(expectedSubstring: "unknown provider", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminToken_WhenConfigured_IsSentAsHeader()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler: handler, token: "s3cret");

        await client.GetProvidersAsync(TestContext.Current.CancellationToken);

        Assert.True(handler.LastRequest!.Headers.TryGetValues(name: "X-Admin-Token", values: out var values));
        Assert.Equal(expected: "s3cret", actual: Assert.Single(values));
    }

    [Fact]
    public async Task AdminToken_WhenNotConfigured_IsNotSent()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.GetProvidersAsync(TestContext.Current.CancellationToken);

        Assert.False(handler.LastRequest!.Headers.Contains("X-Admin-Token"));
    }

    [Fact]
    public async Task TransportFailure_ThrowsWithTheUnderlyingExceptionAsInnerException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetProvidersAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expectedSubstring: "Could not reach the proxy management API", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task MalformedJsonResponse_ThrowsWithTheParseError()
    {
        var handler = new StubHandler(_ => Json("{ not valid json"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetProvidersAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expectedSubstring: "unreadable response", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task NullJsonResponse_ThrowsAnEmptyResponseError()
    {
        var handler = new StubHandler(_ => Json("null"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetProvidersAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expectedSubstring: "empty response", actualString: ex.Message,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorResponse_WithNonJsonBody_FallsBackToRawBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(content: "upstream is on fire", encoding: Encoding.UTF8,
                mediaType: "text/plain")
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetProvidersAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected: "upstream is on fire", actual: ex.Message);
    }

    [Fact]
    public async Task ErrorResponse_WithEmptyBody_FallsBackToTheStatusCode()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(string.Empty)
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetProvidersAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected: "The proxy management API returned 404.", actual: ex.Message);
    }

    [Fact]
    public async Task UpsertProviderAsync_SerializesProviderName_OnTheWire()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        await client.UpsertProviderAsync(
            key: "openai",
            body: new ProviderWriteRequest(null, null, ProviderName: "OpenAI API"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expectedSubstring: "\"providerName\":\"OpenAI API\"", actualString: handler.LastBody,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProvidersAsync_DeserializesProviderName()
    {
        const string json = """
                            {
                              "providers": [
                                {
                                  "key": "openai",
                                  "name": "OpenAI API",
                                  "baseUrl": "https://api.openai.com",
                                  "authHeaderName": "Authorization",
                                  "models": [],
                                  "headers": []
                                }
                              ]
                            }
                            """;
        var handler = new StubHandler(_ => Json(json));
        var client = CreateClient(handler);

        var provider = Assert.Single(await client.GetProvidersAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected: "OpenAI API", actual: provider.Name);
    }

    [Fact]
    public async Task GetProvidersAsync_MissingName_DeserializesAsNull()
    {
        var handler = new StubHandler(_ => Json(ProvidersJson));
        var client = CreateClient(handler);

        var provider = Assert.Single(await client.GetProvidersAsync(TestContext.Current.CancellationToken));

        Assert.Null(provider.Name);
    }

    [Fact]
    public async Task GetRateLimitHistoryAsync_DeserializesDimensionsAndSendsExpectedUrl()
    {
        const string HistoryJson = """
                                   {
                                     "dimensions": {
                                       "tokens": [
                                         { "bucketUtc": "2026-03-01T12:00:00Z", "remaining": 1000, "limit": 2000 },
                                         { "bucketUtc": "2026-03-01T12:01:00Z", "remaining": 900, "limit": 2000 }
                                       ]
                                     }
                                   }
                                   """;
        var handler = new StubHandler(_ => Json(HistoryJson));
        var client = CreateClient(handler);

        var response = await client.GetRateLimitHistoryAsync(key: "openai", 3.5,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HttpMethod.Get, actual: handler.LastRequest!.Method);
        Assert.Contains(expectedSubstring: "admin/providers/openai/rate-limit-history",
            actualString: handler.LastRequest.RequestUri!.ToString());
        Assert.Contains(expectedSubstring: "hours=3.5", actualString: handler.LastRequest.RequestUri!.ToString());
        var points = response.Dimensions["tokens"];
        Assert.Equal(2, actual: points.Count);
        Assert.Equal(1000, actual: points[0].Remaining);
        Assert.Equal(900, actual: points[1].Remaining);
    }

    [Fact]
    public async Task GetRateLimitHistoryAsync_NotFound_ThrowsProviderAdminException()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":{"message":"Provider 'x' not found."}}""", encoding: Encoding.UTF8,
                mediaType: "application/json")
        });
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ProviderAdminException>(() =>
            client.GetRateLimitHistoryAsync(key: "x", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(content: body, encoding: Encoding.UTF8, mediaType: "application/json") };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    /// <summary>A transport that always fails, standing in for a network-level failure (DNS, connection refused, etc.).</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly HttpRequestException _exception;

        public ThrowingHandler(HttpRequestException exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }
}