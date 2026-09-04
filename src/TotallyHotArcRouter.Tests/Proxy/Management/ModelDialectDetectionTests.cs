using Moq;
using System.Net;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers how <see cref="ManagementFacade"/> drives tier 1-3 dialect detection
/// (<c>docs/router/tool-call-normalization.md</c> Phase 3): the sweep over a provider's models on an
/// explicit capability scan, and the single-model classification when a model is added.
/// <para>
/// <see cref="TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling.ModelDialectResolverTests"/> owns the detection logic itself. What is tested here is the
/// wiring around it, and the guarantee that matters is the same one Phase 2's scan carries: detection is an
/// optimization over Phase 4's live observation, so a failure must cost one request's worth of scanning and
/// never the save that triggered it.
/// </para>
/// </summary>
public sealed class ModelDialectDetectionTests : IDisposable
{
    private const string OpenAiBody = """{"object":"list","data":[{"id":"local-a"}]}""";
    private const string OllamaTagsBody = """{"models":[{"name":"qwen2.5-coder:7b"}]}""";

    // Condensed from the real Ollama Qwen 2.5 template; the tool-call framing is what detection reads.
    private const string QwenTemplate = """
                                        For each function call, return a json object within <tool_call></tool_call> XML tags:
                                        <tool_call>
                                        {"name": <function-name>, "arguments": <args-json-object>}
                                        </tool_call>
                                        """;

    private readonly TempDatabase _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>An Ollama-shaped provider whose native paths all answer, serving <paramref name="models"/>.</summary>
    private static InMemoryProviderConfigStore StoreWith(params string[] models)
    {
        return new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["ollama"] = new() { BaseUrl = "http://localhost:11434/v1" }
            },
            ModelList =
            [
                .. models.Select(m => new ModelRouteEntry
                {
                    ModelName = m,
                    Provider = "ollama",
                    ProviderModelId = m
                })
            ]
        });
    }

    /// <summary>Serves the OpenAI list, Ollama's tag list, and a Qwen template from <c>/api/show</c>.</summary>
    private static HttpMessageHandler OllamaServing(string template)
    {
        return new DelegatingHandlerStub(request =>
        {
            var url = request.RequestUri!.ToString();
            string? body = null;
            if (url.EndsWith(value: "/v1/models", comparisonType: StringComparison.Ordinal))
                body = OpenAiBody;
            else if (url.EndsWith(value: "/api/tags", comparisonType: StringComparison.Ordinal))
                body = OllamaTagsBody;
            else if (url.EndsWith(value: "/api/show", comparisonType: StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { template });

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
    }

    /// <summary>
    /// As <see cref="OllamaServing"/>, but the <c>/api/show</c> body also carries the <c>model_info</c>
    /// block a real Ollama returns - the source of the context window recorded alongside the dialect.
    /// </summary>
    private static HttpMessageHandler OllamaServingWithModelInfo(
        string? template, string architecture, long contextLength)
    {
        return new DelegatingHandlerStub(request =>
        {
            var url = request.RequestUri!.ToString();
            string? body = null;
            if (url.EndsWith(value: "/v1/models", comparisonType: StringComparison.Ordinal))
                body = OpenAiBody;
            else if (url.EndsWith(value: "/api/tags", comparisonType: StringComparison.Ordinal))
                body = OllamaTagsBody;
            else if (url.EndsWith(value: "/api/show", comparisonType: StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["template"] = template,
                    ["model_info"] = new Dictionary<string, object>
                    {
                        ["general.architecture"] = architecture,
                        [$"{architecture}.context_length"] = contextLength
                    }
                });

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
    }

    /// <summary>
    /// Builds a facade whose endpoint scanner and management client share <paramref name="handler"/>, so one
    /// stub serves both the flavor probes and the metadata probes that read the flags they record.
    /// </summary>
    private static ManagementFacade Facade(
        IProviderConfigStore store, HttpMessageHandler handler, ToolCallCapabilityStore capabilityStore)
    {
        var environment = Mock.Of<IEnvironmentVariableProvider>();
        return new ManagementFacade(
            store: store,
            environment: environment,
            httpClient: new HttpClient(handler),
            dependencies: new ManagementFacadeDependencies
            {
                EndpointScanner =
                    new ProviderEndpointScanner(httpClient: new HttpClient(handler), environment: environment),
                CapabilityStore = capabilityStore
            });
    }

    private ToolCallCapabilityStore CapabilityStore()
    {
        return _temp.CreateToolCallCapabilityStore();
    }

    // ----- The sweep on an explicit capability scan -----

    [Fact]
    public async Task ScanCapabilities_ClassifiesEveryModelOnTheProvider()
    {
        var capabilities = CapabilityStore();
        var facade = Facade(store: StoreWith("qwen2.5-coder:7b", "qwen3:8b"), handler: OllamaServing(QwenTemplate),
            capabilityStore: capabilities);

        var result =
            await facade.ScanCapabilitiesAsync(key: "ollama", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Value!.OllamaNative);

        foreach (var model in new[] { "qwen2.5-coder:7b", "qwen3:8b" })
        {
            var capability = capabilities.GetModelCapability(providerKey: "ollama", modelName: model);
            Assert.NotNull(capability);
            Assert.Equal(expected: "hermes", actual: capability.Dialect);
            Assert.Equal(expected: DetectionConfidence.Template, actual: capability.Confidence);
        }
    }

    [Fact]
    public async Task ScanCapabilities_WritesNoRow_ForAModelItCannotClassify()
    {
        // The deliberate design: a missing row means "forward natively and classify from the first real
        // response", which beats a guess that would arm the wrong scanner against every response.
        //
        // The template here renders tools in framing the registry does not know - the DeepSeek shape. Phase
        // 5 split what "cannot classify" used to cover: a template with *no* tool support at all now
        // selects emulation (below), so this case needs a template that plainly has tools to still be the
        // "no row" one. Before that split this test passed with a placeholder string, which happened to
        // exercise the other branch.
        var capabilities = CapabilityStore();
        var facade = Facade(
            store: StoreWith("some-private-finetune"),
            handler: OllamaServing("{{- if .Tools }}<|tool calls begin|>{{ .Tools }}<|tool calls end|>{{ end }}"),
            capabilityStore: capabilities);

        await facade.ScanCapabilitiesAsync(key: "ollama", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(capabilities.GetModelCapability(providerKey: "ollama", modelName: "some-private-finetune"));
    }

    [Fact]
    public async Task ScanCapabilities_SelectsEmulation_ForAModelWhoseTemplateRendersNoTools()
    {
        // The Phase 5 selection signal, end to end through the facade sweep: the chat template is the
        // mechanism that would render a tool schema, and this one has no path by which any schema could
        // reach the model - so emulation is the only way it ever calls a tool.
        var capabilities = CapabilityStore();
        var facade = Facade(
            store: StoreWith("some-private-finetune"),
            handler: OllamaServing("{{ .System }}\n{{ .Prompt }}"),
            capabilityStore: capabilities);

        await facade.ScanCapabilitiesAsync(key: "ollama", cancellationToken: TestContext.Current.CancellationToken);

        var capability = capabilities.GetModelCapability(providerKey: "ollama", modelName: "some-private-finetune");
        Assert.Equal(expected: "emulated", actual: capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Template, actual: capability.Confidence);
    }

    [Fact]
    public async Task ScanCapabilities_StillReportsTheEndpointFlavors_WhenDetectionThrows()
    {
        // The operator asked which flavors the endpoint answers. Detection rides along on that answer, so a
        // failure in the passenger must not fail the trip.
        var capabilities = CapabilityStore();
        var flavorsOnly = new DelegatingHandlerStub(request =>
            request.RequestUri!.ToString().EndsWith(value: "/api/show", comparisonType: StringComparison.Ordinal)
                ? throw new HttpRequestException("connection reset")
                : Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OpenAiBody) }));

        var facade = Facade(store: StoreWith("qwen2.5-coder:7b"), handler: flavorsOnly, capabilityStore: capabilities);

        var result =
            await facade.ScanCapabilitiesAsync(key: "ollama", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Value!.OpenAiCompatible);
    }

    [Fact]
    public async Task Detection_DoesNotOverwriteAnOperatorPin()
    {
        // The pin is the escape hatch for a model whose detection misfires, and it is worthless if the next
        // scan silently undoes it. Enforced by the store's confidence gate; asserted here because this is
        // the path that would actually trip it.
        var capabilities = CapabilityStore();
        capabilities.TryRecordModelCapability(new ModelToolCapability(
            ProviderKey: "ollama", ModelName: "qwen2.5-coder:7b", Dialect: "mistral",
            Confidence: DetectionConfidence.Operator, Evidence: "pinned by hand"));

        var facade = Facade(store: StoreWith("qwen2.5-coder:7b"), handler: OllamaServing(QwenTemplate),
            capabilityStore: capabilities);
        await facade.ScanCapabilitiesAsync(key: "ollama", cancellationToken: TestContext.Current.CancellationToken);

        var capability = capabilities.GetModelCapability(providerKey: "ollama", modelName: "qwen2.5-coder:7b");
        Assert.Equal(expected: "mistral", actual: capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Operator, actual: capability.Confidence);
    }

    // ----- Classification when a model is added -----

    [Fact]
    public async Task UpsertModel_ClassifiesTheModelItAdded()
    {
        var capabilities = CapabilityStore();
        var store = StoreWith();
        var facade = Facade(store: store, handler: OllamaServing(QwenTemplate), capabilityStore: capabilities);

        // The provider must have been scanned first: adding a model says nothing new about the provider, so
        // no flavor scan is triggered here and detection reads whatever flags are already recorded.
        await facade.ScanCapabilitiesAsync(key: "ollama", cancellationToken: TestContext.Current.CancellationToken);

        var result = await facade.UpsertModelAsync(
            providerKey: "ollama", modelName: "qwen2.5-coder:7b", request: new ModelWriteRequest(null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(expected: "hermes",
            actual: capabilities.GetModelCapability(providerKey: "ollama", modelName: "qwen2.5-coder:7b")!.Dialect);
    }

    [Fact]
    public async Task UpsertModel_FallsBackToTheModelId_WhenTheProviderHasNeverBeenScanned()
    {
        // No endpoint flags means no native probe is attempted at all - so this asserts the tier-3 path
        // reached through the facade, and that a never-scanned provider still gets a usable classification.
        var capabilities = CapabilityStore();
        var facade = Facade(store: StoreWith(), handler: OllamaServing(QwenTemplate), capabilityStore: capabilities);

        await facade.UpsertModelAsync(
            providerKey: "ollama", modelName: "mixtral-8x7b", request: new ModelWriteRequest(null),
            cancellationToken: TestContext.Current.CancellationToken);

        var capability = capabilities.GetModelCapability(providerKey: "ollama", modelName: "mixtral-8x7b");
        Assert.Equal(expected: "mistral", actual: capability!.Dialect);
        Assert.Equal(expected: DetectionConfidence.Heuristic, actual: capability.Confidence);
    }

    [Fact]
    public async Task UpsertModel_StillSucceeds_WhenDetectionThrows()
    {
        var capabilities = CapabilityStore();
        var alwaysThrows = new DelegatingHandlerStub(_ => throw new InvalidOperationException("boom"));
        var facade = Facade(store: StoreWith(), handler: alwaysThrows, capabilityStore: capabilities);

        var result = await facade.UpsertModelAsync(
            providerKey: "ollama", modelName: "qwen2.5-coder:7b", request: new ModelWriteRequest(null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task UpsertModel_StillSucceeds_WhenNoCapabilityStoreIsConfigured()
    {
        var store = StoreWith();
        var facade = new ManagementFacade(store: store, environment: Mock.Of<IEnvironmentVariableProvider>(),
            httpClient: new HttpClient());

        var result = await facade.UpsertModelAsync(
            providerKey: "ollama", modelName: "qwen2.5-coder:7b", request: new ModelWriteRequest(null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains(collection: store.Snapshot.Options.ModelList, filter: m => m.ModelName == "qwen2.5-coder:7b");
    }

    // ----- Context windows persisted by the same sweep -----

    // The only test that proves the ManagementFacade funnel actually reaches the database: everything else
    // either uses a fake store or exercises ModelDialectResolver in isolation. If step 6's wiring were
    // dropped, this is what would catch it.
    [Fact]
    public async Task ScanCapabilities_PersistsTheContextWindow_AlongsideTheDialect()
    {
        var capabilities = CapabilityStore();
        var facade = Facade(
            store: StoreWith("qwen2.5-coder:7b"),
            handler: OllamaServingWithModelInfo(template: QwenTemplate, architecture: "qwen2", 32768),
            capabilityStore: capabilities);

        var result =
            await facade.ScanCapabilitiesAsync(key: "ollama", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(expected: "hermes",
            actual: capabilities.GetModelCapability(providerKey: "ollama", modelName: "qwen2.5-coder:7b")!.Dialect);

        var window = capabilities.GetModelContextWindow(providerKey: "ollama", modelName: "qwen2.5-coder:7b");
        Assert.NotNull(window);
        Assert.Equal(32768, actual: window.ContextLength);
        Assert.Equal(expected: "qwen2", actual: window.Architecture);
    }

    // The independence guarantee, end to end: a template rendering tools in an unregistered dialect writes
    // no capability row at all, but the window read from the same response must still be persisted. This is
    // the exit that discarded the entire probe before this change.
    [Fact]
    public async Task ScanCapabilities_ThatLearnsNoDialect_StillPersistsTheContextWindow()
    {
        var capabilities = CapabilityStore();
        var facade = Facade(
            store: StoreWith("some-private-finetune"),
            handler: OllamaServingWithModelInfo(
                template: "{{- if .Tools }}<|tool calls begin|>{{ .Tools }}<|tool calls end|>{{ end }}",
                architecture: "deepseek2", 65536),
            capabilityStore: capabilities);

        await facade.ScanCapabilitiesAsync(key: "ollama", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(capabilities.GetModelCapability(providerKey: "ollama", modelName: "some-private-finetune"));
        Assert.Equal(65536,
            actual: capabilities.GetModelContextWindow(providerKey: "ollama", modelName: "some-private-finetune")
                ?.ContextLength);
    }

    // A provider that reports no model_info must leave no row, so a later successful probe is the first
    // thing to write one - the "a probe that read nothing writes nothing" invariant, through the facade.
    [Fact]
    public async Task ScanCapabilities_WritesNoContextWindow_WhenTheProviderReportsNone()
    {
        var capabilities = CapabilityStore();
        var facade = Facade(store: StoreWith("qwen2.5-coder:7b"), handler: OllamaServing(QwenTemplate),
            capabilityStore: capabilities);

        await facade.ScanCapabilitiesAsync(key: "ollama", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(capabilities.GetModelCapability(providerKey: "ollama", modelName: "qwen2.5-coder:7b"));
        Assert.Null(capabilities.GetModelContextWindow(providerKey: "ollama", modelName: "qwen2.5-coder:7b"));
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}