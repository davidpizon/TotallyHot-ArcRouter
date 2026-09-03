using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Integration coverage for the <c>/admin/*</c> management API (see
/// <c>TotallyHot.ArcRouter.Proxy.Management.ProviderAdminEndpoints</c>): drives the real endpoints over HTTP
/// against a booted <see cref="ProxyServer"/> whose management API and LLM-forwarding resolver share a
/// single <see cref="IProviderConfigStore"/>, so an edit through <c>/admin</c> is observably reflected
/// live in <c>/v1/models</c> without a restart.
/// </summary>
[Collection("ProxyLifecycle")]
[Trait(name: "Category", value: "Integration")]
public sealed class ProviderAdminEndpointsTests
{
    private static ModelRoutingOptions SeedOptions()
    {
        return new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { BaseUrl = "https://api.openai.com", AuthHeaderName = "Authorization" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }
            ]
        };
    }

    private static ProxyServer BuildServer(
        IProviderConfigStore store,
        HttpClient? managementHttpClient = null,
        string? managementToken = null,
        ProviderBudgetStore? budgetStore = null)
    {
        var environment = Mock.Of<IEnvironmentVariableProvider>();
        var resolver = new ModelRouteResolver(store: store, environment: environment);
        var interceptor =
            new RequestInterceptor(logger: NullLogger<RequestInterceptor>.Instance, modelRouteResolver: resolver);
        var middleware = new ProxyMiddleware(logger: NullLogger<ProxyMiddleware>.Instance, interceptor: interceptor);

        return new ProxyServer(
            logger: NullLogger<ProxyServer>.Instance,
            proxyMiddleware: middleware,
            0,
            0,
            dependencies: new ProxyServerDependencies
            {
                ManagementToken = managementToken,
                ManagementApi = new ManagementApiDependencies(store)
                {
                    Environment = environment,
                    HttpClient = managementHttpClient,
                    BudgetStore = budgetStore
                }
            });
    }

    private static string BaseAddress(ProxyServer server)
    {
        return server.Addresses.Single(a => a.StartsWith(value: "http://", comparisonType: StringComparison.Ordinal))
            .TrimEnd('/');
    }

    [Fact]
    public async Task GetProviders_ReturnsProvidersWithModels()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync(requestUri: $"{BaseAddress(server)}/admin/providers",
                cancellationToken: TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(json);
            var provider = document.RootElement.GetProperty("providers").EnumerateArray().Single();

            Assert.Equal(expected: "openai", actual: provider.GetProperty("key").GetString());
            Assert.Equal(expected: "https://api.openai.com", actual: provider.GetProperty("baseUrl").GetString());
            Assert.Equal(expected: "gpt-5.4",
                actual: provider.GetProperty("models").EnumerateArray().Single().GetProperty("modelName").GetString());
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GetRateLimitHistory_NoPriceCatalogRepository_ReturnsServiceUnavailable()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/openai/rate-limit-history",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.ServiceUnavailable, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GetRateLimitHistory_UnknownProvider_ReturnsNotFound()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/does-not-exist/rate-limit-history",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.NotFound, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PutProvider_ThenPutModel_IsReflectedLiveInModelsEndpoint()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var baseAddress = BaseAddress(server);

            var putProvider = await client.PutAsync(
                requestUri: $"{baseAddress}/admin/providers/ollama",
                content: JsonBody(new { baseUrl = "http://localhost:11434/v1", authHeaderName = "Authorization" }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: putProvider.StatusCode);

            var putModel = await client.PutAsync(
                requestUri: $"{baseAddress}/admin/providers/ollama/models/llama3",
                content: JsonBody(new { providerModelId = "llama3" }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: putModel.StatusCode);

            // The forwarding resolver shares the same store, so /v1/models reflects the edit with no restart.
            var models = await client.GetStringAsync(requestUri: $"{baseAddress}/v1/models",
                cancellationToken: TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(models);
            var ids = document.RootElement.GetProperty("data").EnumerateArray()
                .Select(e => e.GetProperty("id").GetString()).ToList();
            Assert.Contains(expected: "llama3", collection: ids);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PutProvider_SetsAndClearsIsFree_AndSurfacesItOnGet()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            var set = await client.PutAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/openai",
                content: JsonBody(new { baseUrl = "https://api.openai.com", isFree = true }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: set.StatusCode);
            Assert.True(store.Snapshot.Options.Providers["openai"].IsFree);

            var read = await client.GetStringAsync(requestUri: $"{BaseAddress(server)}/admin/providers",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains(expectedSubstring: "\"isFree\":true", actualString: read,
                comparisonType: StringComparison.Ordinal);

            var clear = await client.PutAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/openai",
                content: JsonBody(new { baseUrl = "https://api.openai.com", isFree = false }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: clear.StatusCode);
            Assert.False(store.Snapshot.Options.Providers["openai"].IsFree);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // isFree is nullable on the wire so that a partial write - the GUI editing only the BaseUrl, or a
    // legacy caller that predates the flag - can't silently un-free a provider and start reporting a
    // paid-looking unknown cost for a local model.
    [Fact]
    public async Task PutProvider_OmittingIsFree_PreservesExistingValue()
    {
        var seed = SeedOptions();
        seed.Providers["openai"] = new ProviderOptions { BaseUrl = "https://api.openai.com", IsFree = true };
        var store = new InMemoryProviderConfigStore(seed);
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            var response = await client.PutAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/openai",
                content: JsonBody(new { baseUrl = "https://api.openai.com/v2" }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);

            var stored = store.Snapshot.Options.Providers["openai"];
            Assert.Equal(expected: "https://api.openai.com/v2", actual: stored.BaseUrl);
            Assert.True(stored.IsFree);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // Custom headers round-trip: a PUT stores the full set (literal + env-var), a GET returns them, a
    // subsequent PUT with a headers array replaces the set, and a PUT that omits headers keeps them.
    [Fact]
    public async Task PutProvider_StoresReplacesAndKeepsCustomHeaders()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var url = $"{BaseAddress(server)}/admin/providers/openai";

            // Store two headers - one literal, one env-var-backed.
            var put = await client.PutAsync(requestUri: url, content: JsonBody(new
            {
                baseUrl = "https://api.openai.com",
                headers = new object[]
                {
                    new { name = "anthropic-version", value = "2023-06-01" },
                    new { name = "X-Secret", valueEnvVar = "SECRET_VAR" }
                }
            }), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: put.StatusCode);

            var stored = store.Snapshot.Options.Providers["openai"].Headers;
            Assert.Equal(2, actual: stored.Count);
            Assert.Equal(expected: "2023-06-01", actual: stored.Single(h => h.Name == "anthropic-version").Value);
            Assert.Equal(expected: "SECRET_VAR", actual: stored.Single(h => h.Name == "X-Secret").ValueEnvVar);

            // A GET returns them (literal value included; env-var header carries only the var name).
            var get = await client.GetAsync(requestUri: $"{BaseAddress(server)}/admin/providers",
                cancellationToken: TestContext.Current.CancellationToken);
            using var doc =
                JsonDocument.Parse(await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var openai = doc.RootElement.GetProperty("providers").EnumerateArray()
                .Single(p => p.GetProperty("key").GetString() == "openai");
            Assert.Equal(2, actual: openai.GetProperty("headers").GetArrayLength());

            // A headers array replaces the whole set.
            await client.PutAsync(requestUri: url,
                content: JsonBody(new
                {
                    baseUrl = "https://api.openai.com",
                    headers = new object[] { new { name = "Only-One", value = "1" } }
                }), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: "Only-One",
                actual: Assert.Single(store.Snapshot.Options.Providers["openai"].Headers).Name);

            // Omitting headers keeps the existing set (legacy callers).
            await client.PutAsync(requestUri: url, content: JsonBody(new { baseUrl = "https://api.openai.com/v2" }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: "Only-One",
                actual: Assert.Single(store.Snapshot.Options.Providers["openai"].Headers).Name);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // A blank provider key reaching the store throws ArgumentException; the endpoint maps that to a 400
    // with an error envelope rather than letting it surface as an unhandled 500.
    [Fact]
    public async Task PutProvider_BlankKey_ReturnsBadRequestNotServerError()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            // "%20" decodes to a whitespace key, which the store rejects with ArgumentException.
            var response = await client.PutAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/%20",
                content: JsonBody(new { baseUrl = "https://example.com" }),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.BadRequest, actual: response.StatusCode);
            using var document =
                JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(expected: "invalid_request_error",
                actual: document.RootElement.GetProperty("error").GetProperty("type").GetString());
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DeleteProvider_StillReferencedByModel_CascadesAndSucceeds()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            // "openai" still owns the "gpt-5.4" model. The removal cascades to it rather than being
            // rejected, so the caller gets a success and no orphaned model is left in the config.
            var response = await client.DeleteAsync(requestUri: $"{BaseAddress(server)}/admin/providers/openai",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
            Assert.False(store.Snapshot.Options.Providers.ContainsKey("openai"));
            Assert.DoesNotContain(collection: store.Snapshot.Options.ModelList, filter: m => m.ModelName == "gpt-5.4");
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DiscoverModels_ParsesOpenAiShapedModelList()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var stub = new DelegatingHandlerStub(request =>
        {
            Assert.Equal(expected: "https://api.openai.com/v1/models", actual: request.RequestUri!.ToString());
            // openai has no custom headers configured, so none are sent (no provider-specific defaults).
            Assert.False(request.Headers.Contains("anthropic-version"));
            const string body = """{ "object": "list", "data": [ { "id": "gpt-5.4" }, { "id": "gpt-4o" } ] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: body, encoding: Encoding.UTF8, mediaType: "application/json")
            });
        });

        using var server = BuildServer(store: store, managementHttpClient: new HttpClient(stub));
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.PostAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/openai/discover-models",
                null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
            using var document =
                JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(document.RootElement.GetProperty("supported").GetBoolean());
            var models = document.RootElement.GetProperty("models").EnumerateArray().Select(e => e.GetString())
                .ToList();
            Assert.Equal(expected: ["gpt-5.4", "gpt-4o"], actual: models);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DiscoverModels_ProviderReturnsError_ReportsUnsupportedWithoutFailing()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var stub = new DelegatingHandlerStub(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        using var server = BuildServer(store: store, managementHttpClient: new HttpClient(stub));
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.PostAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/openai/discover-models",
                null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
            using var document =
                JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.False(document.RootElement.GetProperty("supported").GetBoolean());
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // Discovery sends a provider's configured custom headers (generalized, not host-special-cased). This
    // is how Anthropic's required anthropic-version header - which its /v1/models 400s without - gets sent.
    [Fact]
    public async Task DiscoverModels_SendsConfiguredCustomHeaders()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic"] = new()
                {
                    BaseUrl = "https://api.anthropic.com",
                    AuthHeaderName = "x-api-key",
                    Headers =
                    [
                        new ProviderHeader { Name = "x-api-key", Value = "sk-ant-test" },
                        new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01" }
                    ]
                }
            },
            ModelList = []
        };
        var store = new InMemoryProviderConfigStore(options);
        var stub = new DelegatingHandlerStub(request =>
        {
            Assert.Equal(expected: "https://api.anthropic.com/v1/models", actual: request.RequestUri!.ToString());
            Assert.Equal(expected: "2023-06-01", actual: Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.True(request.Headers.Contains("x-api-key"));
            const string body =
                """{ "data": [ { "id": "claude-opus-4-6", "type": "model" }, { "id": "claude-sonnet-4-6", "type": "model" } ] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content: body, encoding: Encoding.UTF8, mediaType: "application/json")
            });
        });

        using var server = BuildServer(store: store, managementHttpClient: new HttpClient(stub));
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.PostAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/anthropic/discover-models",
                null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
            using var document =
                JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(document.RootElement.GetProperty("supported").GetBoolean());
            var models = document.RootElement.GetProperty("models").EnumerateArray().Select(e => e.GetString())
                .ToList();
            Assert.Equal(expected: ["claude-opus-4-6", "claude-sonnet-4-6"], actual: models);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ManagementToken_WhenConfigured_GatesAdminRequests()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, managementToken: "s3cret");
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var baseAddress = BaseAddress(server);

            var unauthorized = await client.GetAsync(requestUri: $"{baseAddress}/admin/providers",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.Unauthorized, actual: unauthorized.StatusCode);

            using var authorizedRequest =
                new HttpRequestMessage(method: HttpMethod.Get, requestUri: $"{baseAddress}/admin/providers");
            authorizedRequest.Headers.Add(name: "X-Admin-Token", value: "s3cret");
            var authorized = await client.SendAsync(request: authorizedRequest,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: authorized.StatusCode);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // A whitespace-only token (e.g. a misconfigured environment variable expanding to " ") must be treated
    // the same as "not configured" - not as "configured but always fails". ManagementAccessToken.Verify
    // throws on a whitespace expected value, so gating on IsNullOrEmpty instead of IsNullOrWhiteSpace would
    // 500 on every /admin/* request rather than serving it unauthenticated.
    [Fact]
    public async Task ManagementToken_WhitespaceOnly_IsTreatedAsNotConfigured()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, managementToken: "   ");
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(requestUri: $"{BaseAddress(server)}/admin/providers",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // Hardening /admin/* with a token must never affect LLM forwarding: the token filter is scoped to the
    // /admin group only, so real client traffic (which never targets /admin) keeps working with no token,
    // even while /admin/* itself requires one.
    [Fact]
    public async Task ManagementToken_WhenConfigured_DoesNotGateForwardingEndpoints()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store: store, managementToken: "s3cret");
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var baseAddress = BaseAddress(server);

            // No X-Admin-Token header sent, yet /v1/models (answered locally, not proxied) still succeeds.
            var models = await client.GetAsync(requestUri: $"{baseAddress}/v1/models",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: models.StatusCode);

            // Meanwhile /admin/* still requires the token.
            var admin = await client.GetAsync(requestUri: $"{baseAddress}/admin/providers",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.Unauthorized, actual: admin.StatusCode);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // The management API's HeaderView is write-only for secrets (ManagementFacade/HeaderValueSource): a
    // literal header value must never appear in the response, only whether one is set.
    [Fact]
    public async Task GetProviders_NeverReturnsLiteralHeaderValue()
    {
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new()
                {
                    BaseUrl = "https://api.openai.com",
                    Headers = [new ProviderHeader { Name = "X-Literal", Value = "literal-header-secret" }]
                }
            },
            ModelList = []
        };
        var store = new InMemoryProviderConfigStore(options);
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync(requestUri: $"{BaseAddress(server)}/admin/providers",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain(expectedSubstring: "literal-header-secret", actualString: json,
                comparisonType: StringComparison.Ordinal);

            using var document = JsonDocument.Parse(json);
            var header = document.RootElement.GetProperty("providers").EnumerateArray().Single()
                .GetProperty("headers").EnumerateArray().Single();
            Assert.Equal(expected: "literal", actual: header.GetProperty("source").GetString());
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PutBudget_PersistsCaps_AndGetSurfacesCapsAndCurrentMonthSpend()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var temp = new TempDatabase();
        var budgetStore = temp.CreateBudgetStore();
        using var server = BuildServer(store: store, budgetStore: budgetStore);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var baseAddress = BaseAddress(server);

            var put = await client.PutAsync(
                requestUri: $"{baseAddress}/admin/providers/openai/budget",
                content: JsonBody(new { dollarCap = 500m, tokenCap = 1_000_000L }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: put.StatusCode);

            // Simulate a served request billing this provider, as ProxyMiddleware would post-response.
            await budgetStore.RecordUsageAsync(providerKey: "openai", 12.50m, 300, 200, null, null,
                usageAtUtc: DateTimeOffset.UtcNow, cancellationToken: TestContext.Current.CancellationToken);

            var get = await client.GetStringAsync(requestUri: $"{baseAddress}/admin/providers",
                cancellationToken: TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(get);
            var openai = document.RootElement.GetProperty("providers").EnumerateArray()
                .Single(p => p.GetProperty("key").GetString() == "openai");

            Assert.Equal(500m, actual: openai.GetProperty("dollarCap").GetDecimal());
            Assert.Equal(1_000_000L, actual: openai.GetProperty("tokenCap").GetInt64());
            Assert.Equal(12.50m, actual: openai.GetProperty("dollarSpent").GetDecimal());
            Assert.Equal(500L, actual: openai.GetProperty("tokensUsed").GetInt64());
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PutEnabled_TogglesFlag_AndGetSurfacesIt()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var baseAddress = BaseAddress(server);

            // A provider seeded without the flag reads as enabled - the default is what keeps an existing
            // model-routing.json from coming back with every provider silently stopped.
            Assert.True(await ReadEnabledAsync(client: client, baseAddress: baseAddress, key: "openai"));

            var off = await client.PutAsync(
                requestUri: $"{baseAddress}/admin/providers/openai/enabled",
                content: JsonBody(new { enabled = false }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: off.StatusCode);
            Assert.False(store.Snapshot.Options.Providers["openai"].Enabled);
            Assert.False(await ReadEnabledAsync(client: client, baseAddress: baseAddress, key: "openai"));

            var on = await client.PutAsync(
                requestUri: $"{baseAddress}/admin/providers/openai/enabled",
                content: JsonBody(new { enabled = true }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: on.StatusCode);
            Assert.True(await ReadEnabledAsync(client: client, baseAddress: baseAddress, key: "openai"));
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PutEnabled_PreservesEveryOtherField_IncludingAwsSettings()
    {
        // The whole reason /enabled is its own route rather than a field on PUT /providers/{key}: the
        // generic upsert rebuilds the provider through MergeProvider, which does not carry the Aws* fields
        // across. Toggling a Bedrock provider must not quietly drop its region and credential env vars.
        var options = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["bedrock"] = new()
                {
                    BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com",
                    AuthHeaderName = "Authorization",
                    IsFree = true,
                    Headers = [new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01" }],
                    AwsRegion = "us-east-1",
                    AwsAccessKeyIdEnvVar = "AWS_ACCESS_KEY_ID",
                    AwsSecretAccessKeyEnvVar = "AWS_SECRET_ACCESS_KEY",
                    AwsSessionTokenEnvVar = "AWS_SESSION_TOKEN"
                }
            },
            ModelList = []
        };

        var store = new InMemoryProviderConfigStore(options);
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            var response = await client.PutAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/bedrock/enabled",
                content: JsonBody(new { enabled = false }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);

            var stored = store.Snapshot.Options.Providers["bedrock"];
            Assert.False(stored.Enabled);
            Assert.Equal(expected: "https://bedrock-runtime.us-east-1.amazonaws.com", actual: stored.BaseUrl);
            Assert.True(stored.IsFree);
            Assert.Equal(expected: "anthropic-version", actual: Assert.Single(stored.Headers).Name);
            Assert.Equal(expected: "us-east-1", actual: stored.AwsRegion);
            Assert.Equal(expected: "AWS_ACCESS_KEY_ID", actual: stored.AwsAccessKeyIdEnvVar);
            Assert.Equal(expected: "AWS_SECRET_ACCESS_KEY", actual: stored.AwsSecretAccessKeyEnvVar);
            Assert.Equal(expected: "AWS_SESSION_TOKEN", actual: stored.AwsSessionTokenEnvVar);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PutEnabled_UnknownProvider_ReturnsNotFound()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            var response = await client.PutAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/nope/enabled",
                content: JsonBody(new { enabled = false }),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.NotFound, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PutProvider_WithoutEnabled_KeepsStoppedProviderStopped()
    {
        // A partial write through the generic upsert route must not silently restart a stopped provider,
        // the same rule IsFree follows.
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var baseAddress = BaseAddress(server);

            await client.PutAsync(
                requestUri: $"{baseAddress}/admin/providers/openai/enabled",
                content: JsonBody(new { enabled = false }),
                cancellationToken: TestContext.Current.CancellationToken);

            var response = await client.PutAsync(
                requestUri: $"{baseAddress}/admin/providers/openai",
                content: JsonBody(new { baseUrl = "https://api.openai.com/v2" }),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);

            Assert.False(store.Snapshot.Options.Providers["openai"].Enabled);
            Assert.Equal(expected: "https://api.openai.com/v2",
                actual: store.Snapshot.Options.Providers["openai"].BaseUrl);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<bool> ReadEnabledAsync(HttpClient client, string baseAddress, string key)
    {
        var json = await client.GetStringAsync(requestUri: $"{baseAddress}/admin/providers",
            cancellationToken: TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("providers").EnumerateArray()
            .Single(p => p.GetProperty("key").GetString() == key)
            .GetProperty("enabled").GetBoolean();
    }

    [Fact]
    public async Task PutBudget_NegativeCap_ReturnsBadRequest()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var temp = new TempDatabase();
        using var server = BuildServer(store: store, budgetStore: temp.CreateBudgetStore());
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            var response = await client.PutAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/openai/budget",
                content: JsonBody(new { dollarCap = -1m, tokenCap = (long?)null }),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.BadRequest, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PutBudget_UnknownProvider_ReturnsNotFound()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var temp = new TempDatabase();
        using var server = BuildServer(store: store, budgetStore: temp.CreateBudgetStore());
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            var response = await client.PutAsync(
                requestUri: $"{BaseAddress(server)}/admin/providers/nope/budget",
                content: JsonBody(new { dollarCap = 10m, tokenCap = (long?)null }),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: HttpStatusCode.NotFound, actual: response.StatusCode);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static StringContent JsonBody(object value)
    {
        return new StringContent(content: JsonSerializer.Serialize(value), encoding: Encoding.UTF8,
            mediaType: "application/json");
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