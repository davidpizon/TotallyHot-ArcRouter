using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Integration;

/// <summary>
/// Parity tests that drive TotallyHot.ArcRouter's real proxy against the real
/// <c>litellm-sidecar/</c> and compare behavior, scoped to the four release-gate pillars of an
/// earlier parity workstream (unified API translation, local proxy CLI, cost tracking, local
/// fallbacks). The sidecar is dev/test-only scaffolding that nothing starts
/// automatically (see litellm-sidecar/README.md) - every test here skips itself via
/// <see cref="Assert.SkipUnless"/> when it isn't reachable at <see cref="SidecarBaseUrl"/>, rather
/// than failing, since "sidecar not running" is an expected, non-broken state in CI and on most
/// contributors' machines.
///
/// All four pillars are now built and covered by real parity tests here: unified API
/// translation passthrough, basic token/cost tracking, the Local Proxy CLI single-model override, and -
/// originally the Simple Local Fallbacks work, since superseded by the Circuit Breaker
/// (docs/router/agent-resilience-strategies.md) - a live outage cascade
/// (<see cref="Proxy_FallsBackToBackupProvider_OnPrimaryOutage_LikeLiteLlm"/>) that points a model's
/// primary at a dead port and asserts the request transparently fails over to another configured model on
/// the live sidecar.
/// </summary>
[Collection("ProxyLifecycle")]
[Trait("Category", "Integration")]
public class LiteLlmParityTests
{
    // Explicit IPv4 loopback rather than "localhost": on Windows, "localhost" resolves to IPv6 (::1)
    // first, but a container runtime that only publishes the sidecar's port on IPv4 (e.g. Podman's
    // gvproxy) won't answer there, so the reachability probe would time out and every live test would
    // silently skip. 127.0.0.1 pins the loopback the sidecar is actually reachable on.
    private const string SidecarBaseUrl = "http://127.0.0.1:4000";
    private const string SidecarHealthUrl = SidecarBaseUrl + "/health/liveliness";
    private const string SkipReason =
        "litellm-sidecar not reachable at " + SidecarBaseUrl + " - run `docker compose up` in " +
        "litellm-sidecar/ first (see litellm-sidecar/README.md). This sidecar is dev/test-only " +
        "scaffolding, never started automatically by CI.";

    // One of the eight models litellm-sidecar/config.yaml configures a mock_response for.
    private const string TestModel = "claude-opus-4.6";

    // Must match litellm-sidecar/docker-compose.yml's LITELLM_MASTER_KEY exactly: setting that env
    // var makes the sidecar enforce it as a bearer credential on every request (including
    // mock_response ones - mock_response only skips the *upstream provider* call, not the sidecar's
    // own inbound auth check), so the route TotallyHot.ArcRouter forwards through must present it.
    private const string SidecarMasterKey = "mock-not-a-real-key-parity-master";

    [Fact]
    public async Task Proxy_ForwardsChatCompletion_ToLiteLlmSidecar_AndReturnsItsMockResponse()
    {
        Assert.SkipUnless(await IsSidecarReachableAsync(), SkipReason);

        var modelRouteResolver = ModelRouteResolverTestFactory.Create(
            modelName: TestModel,
            providerModelId: TestModel,
            baseUrl: SidecarBaseUrl,
            apiKey: SidecarMasterKey,
            providerName: "openai");

        await using var proxy = await StartProxyAsync(modelRouteResolver);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = BuildChatCompletionRequest(proxy.BaseUrl, TestModel);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        // The mock text is litellm-sidecar/config.yaml's own fixture, not something TotallyHot.ArcRouter
        // generates - proving this string round-tripped means the proxy forwarded the rewritten
        // request to the sidecar and streamed its real response back unmodified, i.e. the "Unified
        // API Translation" pillar's request/response passthrough works for a real OpenAI-shaped
        // upstream (what litellm's mock_response always returns, regardless of configured provider).
        Assert.Contains($"mock response from {TestModel}", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_TracksCostAndUsage_FromLiteLlmSidecarsRealUsagePayload()
    {
        Assert.SkipUnless(await IsSidecarReachableAsync(), SkipReason);

        var modelRouteResolver = ModelRouteResolverTestFactory.Create(
            modelName: TestModel,
            providerModelId: TestModel,
            baseUrl: SidecarBaseUrl,
            apiKey: SidecarMasterKey,
            providerName: "openai");

        var capturingPublisher = new CapturingTelemetryPublisher();
        await using var proxy = await StartProxyAsync(modelRouteResolver, capturingPublisher);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = BuildChatCompletionRequest(proxy.BaseUrl, TestModel);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var published = await capturingPublisher.WaitForEventAsync(TimeSpan.FromSeconds(5));

        // litellm computes real token counts for its mock_response text (it isn't a fabricated
        // fixture the test writes itself), so a non-null usage here proves TotallyHot.ArcRouter's
        // IUsageExtractor parses a real upstream's response shape correctly, not just a hand-crafted
        // test payload. That's the half of the "Basic Token/Cost Tracking" pillar this test can prove
        // today; the cost half waits on the price catalog (docs/router/model-price-catalog.md).
        Assert.NotNull(published.PromptTokens);
        Assert.NotNull(published.CompletionTokens);
        Assert.True(published.PromptTokens!.Value > 0);
        Assert.True(published.CompletionTokens!.Value > 0);

        // This harness wires no price catalog into the proxy (StartProxyAsync passes no IModelPriceLookup),
        // so a paid provider's cost is unknown here rather than estimated - never a placeholder zero. The
        // catalog-backed cost path itself is covered by
        // ProxyMiddlewareTests.InvokeAsync_PaidProviderWithCatalogPrice_RecordsEstimatedCostFromCatalog; a
        // free provider would report 0m instead - see ProviderOptions.IsFree and ModelPrice.Free.
        Assert.Null(published.EstimatedCostUsd);
    }

    [Fact]
    public async Task Proxy_UnknownModel_OutsideSingleModelServing_IsAgenticallyRoutedNotRejected()
    {
        Assert.SkipUnless(await IsSidecarReachableAsync(), SkipReason);

        const string unknownModel = "definitely-not-a-configured-model";

        var modelRouteResolver = ModelRouteResolverTestFactory.Create(
            modelName: TestModel,
            providerModelId: TestModel,
            baseUrl: SidecarBaseUrl,
            apiKey: SidecarMasterKey,
            providerName: "openai");

        await using var proxy = await StartProxyAsync(modelRouteResolver);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        using var proxyRequest = BuildChatCompletionRequest(proxy.BaseUrl, unknownModel);
        using var proxyResponse = await client.SendAsync(proxyRequest, TestContext.Current.CancellationToken);
        var proxyBody = await proxyResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // docs/router/utility-model-routing.md's generalized fallback: outside single-model serving, an
        // unresolved model is accepted and agentically routed to a real, allowlisted candidate - here the
        // only one configured (TestModel) - rather than rejected with a litellm-style 400 error envelope.
        // This is a deliberate TotallyHot.ArcRouter/LiteLLM divergence (litellm has no such fallback), not a
        // parity gap, so this test no longer compares error shapes against the sidecar directly.
        Assert.Equal(HttpStatusCode.OK, proxyResponse.StatusCode);
        Assert.False(string.IsNullOrEmpty(proxyBody));
    }

    [Fact]
    public async Task Cli_SingleModelServing_ForcesClientRequestToConfiguredModel_LikeLiteLlmModelFlag()
    {
        Assert.SkipUnless(await IsSidecarReachableAsync(), SkipReason);

        // Only TestModel is configured; single-model serving (the --model CLI flag) is what makes the
        // proxy ignore whatever model the client asks for and route every request to this one model,
        // mirroring `litellm --model provider/name`. See SingleModelServingOptions / RequestInterceptor.
        var modelRouteResolver = ModelRouteResolverTestFactory.Create(
            modelName: TestModel,
            providerModelId: TestModel,
            baseUrl: SidecarBaseUrl,
            apiKey: SidecarMasterKey,
            providerName: "openai");

        await using var proxy = await StartProxyAsync(modelRouteResolver, forcedModelName: TestModel);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // The client deliberately asks for a DIFFERENT model than the one --model forces. Under normal
        // multi-model routing this unconfigured name would 400; single-model serving must override it.
        using var request = BuildChatCompletionRequest(proxy.BaseUrl, "some-other-model-the-client-picked");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        // Despite the client naming a different model, the sidecar returns TestModel's own mock_response -
        // proving the --model flag force-routed the request to the single configured model rather than
        // honoring (and rejecting) the client's requested model.
        Assert.Contains($"mock response from {TestModel}", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_FallsBackToBackupProvider_OnPrimaryOutage_LikeLiteLlm()
    {
        Assert.SkipUnless(await IsSidecarReachableAsync(), SkipReason);

        // Primary points at a dead loopback port (nothing listens on :9, the discard port) so the very
        // first forward gets a connection refusal - a total upstream outage. The only other configured
        // model routes to the live sidecar, so it's the dynamic next-best candidate (docs/router/
        // agent-resilience-strategies.md's Circuit Breaker), proven end-to-end against a real backup - not
        // the paper's Verifier-driven semantic re-routing.
        var resolver = CreateOutageFallbackResolver();

        await using var proxy = await StartProxyAsync(resolver);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // The client asks for the primary (outage) model; the proxy must transparently fail over.
        using var request = BuildChatCompletionRequest(proxy.BaseUrl, "primary-outage-model");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        // The backup (sidecar) served the request: its own mock_response for TestModel came back, proving
        // the primary's outage was transparently cascaded to the configured backup provider - like
        // LiteLLM's `fallbacks`.
        Assert.Contains($"mock response from {TestModel}", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a resolver whose <c>primary-outage-model</c> points at a dead loopback port, with
    /// <c>backup-live-model</c> as the only other configured model (routing to the live sidecar) for the
    /// dynamic next-best selection to land on. Constructed inline rather than via
    /// <see cref="ModelRouteResolverTestFactory"/> because the backup provider needs the real sidecar
    /// bearer credential, not a placeholder.
    /// </summary>
    private static IModelRouteResolver CreateOutageFallbackResolver()
    {
        var options = new TotallyHot.ArcRouter.Models.ModelRoutingOptions
        {
            Providers = new Dictionary<string, TotallyHot.ArcRouter.Models.ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["dead-primary"] = new TotallyHot.ArcRouter.Models.ProviderOptions
                {
                    // Port 9 (discard) on loopback: nothing listens, so the forward is refused immediately.
                    BaseUrl = "http://127.0.0.1:9",
                    Headers = [new TotallyHot.ArcRouter.Models.ProviderHeader { Name = "Authorization", Value = "Bearer unused-primary-key" }]
                },
                ["live-backup"] = new TotallyHot.ArcRouter.Models.ProviderOptions
                {
                    BaseUrl = SidecarBaseUrl,
                    Headers = [new TotallyHot.ArcRouter.Models.ProviderHeader { Name = "Authorization", Value = $"Bearer {SidecarMasterKey}" }]
                }
            },
            ModelList =
            [
                new TotallyHot.ArcRouter.Models.ModelRouteEntry
                {
                    ModelName = "primary-outage-model",
                    Provider = "dead-primary",
                    ProviderModelId = "primary-upstream-id"
                },
                new TotallyHot.ArcRouter.Models.ModelRouteEntry
                {
                    ModelName = "backup-live-model",
                    Provider = "live-backup",
                    ProviderModelId = TestModel
                }
            ]
        };

        return new ModelRouteResolver(new InMemoryProviderConfigStore(options), Mock.Of<IEnvironmentVariableProvider>());
    }

    private static HttpRequestMessage BuildChatCompletionRequest(string baseUrl, string model)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "user", content = "hi" } }
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}v1/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "client-side-placeholder-token");
        return request;
    }

    private static async Task<bool> IsSidecarReachableAsync()
    {
        // Deliberately does NOT pass TestContext.Current.CancellationToken into the probe itself: if
        // the test run is genuinely being canceled (timeout/abort), that must propagate as a
        // cancellation, not get swallowed by the catch below and misreported as "sidecar
        // unreachable, skip." This check surfaces that case up front; the probe's own 2s HttpClient
        // timeout is what legitimately means "not reachable."
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(SidecarHealthUrl, CancellationToken.None);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Sidecar not running, not listening yet, or unreachable from this environment's egress
            // policy - all of these mean "skip," not "fail."
            return false;
        }
    }

    private static async Task<TestProxy> StartProxyAsync(
        IModelRouteResolver modelRouteResolver,
        ITelemetryPublisher? telemetryPublisher = null,
        string? forcedModelName = null)
    {
        var singleModelServingOptions = forcedModelName is null
            ? null
            : new SingleModelServingOptions { ForcedModelName = forcedModelName };
        var interceptor = new RequestInterceptor(
            NullLogger<RequestInterceptor>.Instance,
            modelRouteResolver,
            singleModelServingOptions);
        var middleware = new ProxyMiddleware(
            NullLogger<ProxyMiddleware>.Instance,
            interceptor,
            dependencies: new ProxyMiddlewareDependencies
            {
                TelemetryPublisher = telemetryPublisher
            }
        );

        // port: 0 / grpcPort: 0 bind ephemeral loopback ports (see ProxyServer's constructor remarks)
        // so this test never flakes on a fixed port already being in use, unlike the disabled
        // ProxyInterceptionTests.Proxy_InterceptsAndForwards_Request_ToUpstream's hardcoded :5001.
        var server = new ProxyServer(NullLogger<ProxyServer>.Instance, middleware, port: 0, grpcPort: 0);
        await server.StartAsync(TestContext.Current.CancellationToken);

        var address = server.Addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal));
        return new TestProxy(server, address.EndsWith('/') ? address : address + "/");
    }

    private sealed record TestProxy(ProxyServer Server, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // A short bounded timeout, not CancellationToken.None, mirroring ProxyServerTests - a
            // stalled shutdown (e.g. after a prior assertion threw mid-test) must never hang the whole
            // test run waiting on it.
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Server.StopAsync(stopCts.Token);
        }
    }

    /// <summary>Captures the single <see cref="RoutingTelemetryEvent"/> a test's one request publishes, so the test can assert on it directly instead of re-deriving it from the HTTP response.</summary>
    private sealed class CapturingTelemetryPublisher : ITelemetryPublisher
    {
        private readonly TaskCompletionSource<RoutingTelemetryEvent> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            _tcs.TrySetResult(telemetryEvent);
            return Task.CompletedTask;
        }

        public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<RoutingTelemetryEvent> WaitForEventAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_tcs.Task, Task.Delay(timeout));
            Assert.True(ReferenceEquals(completed, _tcs.Task), "Timed out waiting for a routing telemetry event to be published.");
            return await _tcs.Task;
        }
    }
}

