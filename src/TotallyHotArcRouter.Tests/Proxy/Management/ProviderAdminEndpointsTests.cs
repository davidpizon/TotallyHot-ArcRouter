using System.Net;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Integration coverage for the <c>/admin/*</c> management API (see
/// <c>TotallyHot.ArcRouter.Proxy.Management.ProviderAdminEndpoints</c>): drives the real endpoints over HTTP
/// against a booted <see cref="ProxyServer"/> whose management API and LLM-forwarding resolver share a
/// single <see cref="IProviderConfigStore"/>, so an edit through <c>/admin</c> is observably reflected
/// live in <c>/v1/models</c> without a restart.
/// </summary>
[Collection("ProxyLifecycle")]
[Trait("Category", "Integration")]
public sealed class ProviderAdminEndpointsTests
{
    private static ModelRoutingOptions SeedOptions() => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new ProviderOptions { BaseUrl = "https://api.openai.com", AuthHeaderName = "Authorization" }
        },
        ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }]
    };

    private static ProxyServer BuildServer(
        IProviderConfigStore store,
        HttpClient? managementHttpClient = null,
        string? managementToken = null,
        TotallyHot.ArcRouter.PriceCatalog.ProviderBudgetStore? budgetStore = null)
    {
        var environment = Mock.Of<IEnvironmentVariableProvider>();
        var resolver = new ModelRouteResolver(store, environment);
        var interceptor = new RequestInterceptor(NullLogger<RequestInterceptor>.Instance, resolver);
        var middleware = new ProxyMiddleware(NullLogger<ProxyMiddleware>.Instance, interceptor);

        return new ProxyServer(
            NullLogger<ProxyServer>.Instance,
            middleware,
            port: 0,
            grpcPort: 0,
            providerConfigStore: store,
            environment: environment,
            managementHttpClient: managementHttpClient,
            managementToken: managementToken,
            providerBudgetStore: budgetStore);
    }

    private static string BaseAddress(ProxyServer server) =>
        server.Addresses.Single(a => a.StartsWith("http://", StringComparison.Ordinal)).TrimEnd('/');

    [Fact]
    public async Task GetProviders_ReturnsProvidersWithModels()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var server = BuildServer(store);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync($"{BaseAddress(server)}/admin/providers", TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(json);
            var provider = document.RootElement.GetProperty("providers").EnumerateArray().Single();

            Assert.Equal("openai", provider.GetProperty("key").GetString());
            Assert.Equal("https://api.openai.com", provider.GetProperty("baseUrl").GetString());
            Assert.Equal("gpt-5.4", provider.GetProperty("models").EnumerateArray().Single().GetProperty("modelName").GetString());
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
                $"{baseAddress}/admin/providers/ollama",
                JsonBody(new { baseUrl = "http://localhost:11434/v1", authHeaderName = "Authorization" }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, putProvider.StatusCode);

            var putModel = await client.PutAsync(
                $"{baseAddress}/admin/providers/ollama/models/llama3",
                JsonBody(new { providerModelId = "llama3" }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, putModel.StatusCode);

            // The forwarding resolver shares the same store, so /v1/models reflects the edit with no restart.
            var models = await client.GetStringAsync($"{baseAddress}/v1/models", TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(models);
            var ids = document.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
            Assert.Contains("llama3", ids);
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
                $"{BaseAddress(server)}/admin/providers/openai",
                JsonBody(new { baseUrl = "https://api.openai.com", isFree = true }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.True(store.Snapshot.Options.Providers["openai"].IsFree);

            var read = await client.GetStringAsync($"{BaseAddress(server)}/admin/providers", TestContext.Current.CancellationToken);
            Assert.Contains("\"isFree\":true", read, StringComparison.Ordinal);

            var clear = await client.PutAsync(
                $"{BaseAddress(server)}/admin/providers/openai",
                JsonBody(new { baseUrl = "https://api.openai.com", isFree = false }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
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
                $"{BaseAddress(server)}/admin/providers/openai",
                JsonBody(new { baseUrl = "https://api.openai.com/v2" }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var stored = store.Snapshot.Options.Providers["openai"];
            Assert.Equal("https://api.openai.com/v2", stored.BaseUrl);
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
            var put = await client.PutAsync(url, JsonBody(new
            {
                baseUrl = "https://api.openai.com",
                headers = new object[]
                {
                    new { name = "anthropic-version", value = "2023-06-01" },
                    new { name = "X-Secret", valueEnvVar = "SECRET_VAR" }
                }
            }), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var stored = store.Snapshot.Options.Providers["openai"].Headers;
            Assert.Equal(2, stored.Count);
            Assert.Equal("2023-06-01", stored.Single(h => h.Name == "anthropic-version").Value);
            Assert.Equal("SECRET_VAR", stored.Single(h => h.Name == "X-Secret").ValueEnvVar);

            // A GET returns them (literal value included; env-var header carries only the var name).
            var get = await client.GetAsync($"{BaseAddress(server)}/admin/providers", TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var openai = doc.RootElement.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("key").GetString() == "openai");
            Assert.Equal(2, openai.GetProperty("headers").GetArrayLength());

            // A headers array replaces the whole set.
            await client.PutAsync(url, JsonBody(new { baseUrl = "https://api.openai.com", headers = new object[] { new { name = "Only-One", value = "1" } } }), TestContext.Current.CancellationToken);
            Assert.Equal("Only-One", Assert.Single(store.Snapshot.Options.Providers["openai"].Headers).Name);

            // Omitting headers keeps the existing set (legacy callers).
            await client.PutAsync(url, JsonBody(new { baseUrl = "https://api.openai.com/v2" }), TestContext.Current.CancellationToken);
            Assert.Equal("Only-One", Assert.Single(store.Snapshot.Options.Providers["openai"].Headers).Name);
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
                $"{BaseAddress(server)}/admin/providers/%20",
                JsonBody(new { baseUrl = "https://example.com" }),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("invalid_request_error", document.RootElement.GetProperty("error").GetProperty("type").GetString());
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
            var response = await client.DeleteAsync($"{BaseAddress(server)}/admin/providers/openai", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(store.Snapshot.Options.Providers.ContainsKey("openai"));
            Assert.DoesNotContain(store.Snapshot.Options.ModelList, m => m.ModelName == "gpt-5.4");
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
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri!.ToString());
            // openai has no custom headers configured, so none are sent (no provider-specific defaults).
            Assert.False(request.Headers.Contains("anthropic-version"));
            const string body = """{ "object": "list", "data": [ { "id": "gpt-5.4" }, { "id": "gpt-4o" } ] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        });

        using var server = BuildServer(store, managementHttpClient: new HttpClient(stub));
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"{BaseAddress(server)}/admin/providers/openai/discover-models",
                content: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(document.RootElement.GetProperty("supported").GetBoolean());
            var models = document.RootElement.GetProperty("models").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(["gpt-5.4", "gpt-4o"], models);
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

        using var server = BuildServer(store, managementHttpClient: new HttpClient(stub));
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"{BaseAddress(server)}/admin/providers/openai/discover-models",
                content: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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
                ["anthropic"] = new ProviderOptions
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
            Assert.Equal("https://api.anthropic.com/v1/models", request.RequestUri!.ToString());
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.True(request.Headers.Contains("x-api-key"));
            const string body = """{ "data": [ { "id": "claude-opus-4-6", "type": "model" }, { "id": "claude-sonnet-4-6", "type": "model" } ] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        });

        using var server = BuildServer(store, managementHttpClient: new HttpClient(stub));
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"{BaseAddress(server)}/admin/providers/anthropic/discover-models",
                content: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(document.RootElement.GetProperty("supported").GetBoolean());
            var models = document.RootElement.GetProperty("models").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(["claude-opus-4-6", "claude-sonnet-4-6"], models);
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
        using var server = BuildServer(store, managementToken: "s3cret");
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var baseAddress = BaseAddress(server);

            var unauthorized = await client.GetAsync($"{baseAddress}/admin/providers", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            using var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseAddress}/admin/providers");
            authorizedRequest.Headers.Add("X-Admin-Token", "s3cret");
            var authorized = await client.SendAsync(authorizedRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
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
        using var server = BuildServer(store, managementToken: "   ");
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"{BaseAddress(server)}/admin/providers", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
        using var server = BuildServer(store, managementToken: "s3cret");
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var baseAddress = BaseAddress(server);

            // No X-Admin-Token header sent, yet /v1/models (answered locally, not proxied) still succeeds.
            var models = await client.GetAsync($"{baseAddress}/v1/models", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, models.StatusCode);

            // Meanwhile /admin/* still requires the token.
            var admin = await client.GetAsync($"{baseAddress}/admin/providers", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, admin.StatusCode);
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
                ["openai"] = new ProviderOptions
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
            var json = await client.GetStringAsync($"{BaseAddress(server)}/admin/providers", TestContext.Current.CancellationToken);

            Assert.DoesNotContain("literal-header-secret", json, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(json);
            var header = document.RootElement.GetProperty("providers").EnumerateArray().Single()
                .GetProperty("headers").EnumerateArray().Single();
            Assert.Equal("literal", header.GetProperty("source").GetString());
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
        using var temp = new TotallyHot.ArcRouter.Tests.PriceCatalog.TempDatabase();
        var budgetStore = temp.CreateBudgetStore();
        using var server = BuildServer(store, budgetStore: budgetStore);
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var baseAddress = BaseAddress(server);

            var put = await client.PutAsync(
                $"{baseAddress}/admin/providers/openai/budget",
                JsonBody(new { dollarCap = 500m, tokenCap = 1_000_000L }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            // Simulate a served request billing this provider, as ProxyMiddleware would post-response.
            await budgetStore.RecordUsageAsync("openai", 12.50m, promptTokens: 300, completionTokens: 200, cacheCreationTokens: null, cacheReadTokens: null, usageAtUtc: DateTimeOffset.UtcNow, cancellationToken: TestContext.Current.CancellationToken);

            var get = await client.GetStringAsync($"{baseAddress}/admin/providers", TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(get);
            var openai = document.RootElement.GetProperty("providers").EnumerateArray()
                .Single(p => p.GetProperty("key").GetString() == "openai");

            Assert.Equal(500m, openai.GetProperty("dollarCap").GetDecimal());
            Assert.Equal(1_000_000L, openai.GetProperty("tokenCap").GetInt64());
            Assert.Equal(12.50m, openai.GetProperty("dollarSpent").GetDecimal());
            Assert.Equal(500L, openai.GetProperty("tokensUsed").GetInt64());
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
            Assert.True(await ReadEnabledAsync(client, baseAddress, "openai"));

            var off = await client.PutAsync(
                $"{baseAddress}/admin/providers/openai/enabled",
                JsonBody(new { enabled = false }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            Assert.False(store.Snapshot.Options.Providers["openai"].Enabled);
            Assert.False(await ReadEnabledAsync(client, baseAddress, "openai"));

            var on = await client.PutAsync(
                $"{baseAddress}/admin/providers/openai/enabled",
                JsonBody(new { enabled = true }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, on.StatusCode);
            Assert.True(await ReadEnabledAsync(client, baseAddress, "openai"));
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
                ["bedrock"] = new ProviderOptions
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
                $"{BaseAddress(server)}/admin/providers/bedrock/enabled",
                JsonBody(new { enabled = false }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var stored = store.Snapshot.Options.Providers["bedrock"];
            Assert.False(stored.Enabled);
            Assert.Equal("https://bedrock-runtime.us-east-1.amazonaws.com", stored.BaseUrl);
            Assert.True(stored.IsFree);
            Assert.Equal("anthropic-version", Assert.Single(stored.Headers).Name);
            Assert.Equal("us-east-1", stored.AwsRegion);
            Assert.Equal("AWS_ACCESS_KEY_ID", stored.AwsAccessKeyIdEnvVar);
            Assert.Equal("AWS_SECRET_ACCESS_KEY", stored.AwsSecretAccessKeyEnvVar);
            Assert.Equal("AWS_SESSION_TOKEN", stored.AwsSessionTokenEnvVar);
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
                $"{BaseAddress(server)}/admin/providers/nope/enabled",
                JsonBody(new { enabled = false }),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
                $"{baseAddress}/admin/providers/openai/enabled",
                JsonBody(new { enabled = false }),
                TestContext.Current.CancellationToken);

            var response = await client.PutAsync(
                $"{baseAddress}/admin/providers/openai",
                JsonBody(new { baseUrl = "https://api.openai.com/v2" }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.False(store.Snapshot.Options.Providers["openai"].Enabled);
            Assert.Equal("https://api.openai.com/v2", store.Snapshot.Options.Providers["openai"].BaseUrl);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<bool> ReadEnabledAsync(HttpClient client, string baseAddress, string key)
    {
        var json = await client.GetStringAsync($"{baseAddress}/admin/providers", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("providers").EnumerateArray()
            .Single(p => p.GetProperty("key").GetString() == key)
            .GetProperty("enabled").GetBoolean();
    }

    [Fact]
    public async Task PutBudget_NegativeCap_ReturnsBadRequest()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        using var temp = new TotallyHot.ArcRouter.Tests.PriceCatalog.TempDatabase();
        using var server = BuildServer(store, budgetStore: temp.CreateBudgetStore());
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            var response = await client.PutAsync(
                $"{BaseAddress(server)}/admin/providers/openai/budget",
                JsonBody(new { dollarCap = -1m, tokenCap = (long?)null }),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        using var temp = new TotallyHot.ArcRouter.Tests.PriceCatalog.TempDatabase();
        using var server = BuildServer(store, budgetStore: temp.CreateBudgetStore());
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();

            var response = await client.PutAsync(
                $"{BaseAddress(server)}/admin/providers/nope/budget",
                JsonBody(new { dollarCap = 10m, tokenCap = (long?)null }),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}

