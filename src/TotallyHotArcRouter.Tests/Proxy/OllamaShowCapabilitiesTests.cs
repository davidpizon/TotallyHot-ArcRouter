using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers what <c>POST /api/show</c> declares about a model: its <c>capabilities</c> array and its
/// <c>model_info</c> context window (<c>docs/router/ollama-show-capabilities-plan.md</c>).
/// <para>
/// The behavior under test is what makes router models selectable in a capability-filtering client's model
/// picker - Visual Studio's Copilot chat drops any model that declares no <c>tools</c> support - so these
/// assertions are the regression guard for the bug that motivated the feature, not just schema checks.
/// </para>
/// </summary>
public class OllamaShowCapabilitiesTests
{
    private const string RouterModel = "totallyhot-arcrouter";

    [Fact]
    public async Task PostOllamaShow_KnownModel_DeclaresCompletionAndTools()
    {
        var root = await ShowAsync(modelName: "gpt-5.4", resolver: ModelList(("gpt-5.4", "openai")));

        var capabilities = Capabilities(root);
        Assert.Equal(expectedSpan: ["completion", "tools"], actualArray: capabilities);
    }

    // The judgment call recorded in ADR-0003: an `emulated` model cannot call tools natively - that row is
    // written precisely because its chat template renders none - but the router emulates them on its
    // behalf, and /api/show describes the endpoint the client is addressing. Declaring false here would
    // hide the router's own emulation feature from the one client that filters on this field.
    [Fact]
    public async Task PostOllamaShow_EmulatedModel_StillDeclaresTools()
    {
        var store = new FakeToolCallCapabilityStore().Seed(new ModelToolCapability(
            ProviderKey: "local", ModelName: "qwen-local", Dialect: ToolCallDialectRegistry.Emulated.Name,
            Confidence: DetectionConfidence.Template));

        var root = await ShowAsync(modelName: "qwen-local", resolver: ModelList(("qwen-local", "local")), store: store);

        Assert.Contains(expected: "tools", collection: Capabilities(root));
    }

    // The dominant state, and the one that decides whether the fix works at all: a fresh install has run no
    // scan, and no hosted provider can be probed. Withholding tools here would filter out exactly the cloud
    // models that unambiguously support them.
    [Fact]
    public async Task PostOllamaShow_UnclassifiedModel_StillDeclaresTools()
    {
        var root = await ShowAsync(modelName: "gpt-5.4", resolver: ModelList(("gpt-5.4", "openai")),
            store: new FakeToolCallCapabilityStore());

        Assert.Contains(expected: "tools", collection: Capabilities(root));
    }

    [Fact]
    public async Task PostOllamaShow_KnownContextLength_IsKeyedByTheProbedArchitecture()
    {
        var store = new FakeToolCallCapabilityStore().SeedContextWindow(providerKey: "local", modelName: "qwen-local",
            32768, architecture: "qwen2");

        var root = await ShowAsync(modelName: "qwen-local", resolver: ModelList(("qwen-local", "local")), store: store);

        var modelInfo = root.GetProperty("model_info");
        Assert.Equal(expected: "qwen2", actual: modelInfo.GetProperty("general.architecture").GetString());
        Assert.Equal(32768, actual: modelInfo.GetProperty("qwen2.context_length").GetInt32());
    }

    // Absent, not null. This endpoint serializes without options, so default handling would emit
    // "model_info": null - which a client can read as "no context limit" rather than "not stated". This is
    // the test that catches a regression to default serialization.
    [Fact]
    public async Task PostOllamaShow_UnknownContextLength_OmitsModelInfoEntirely()
    {
        // A store that is present but has never probed this model - the state of every hosted provider,
        // which publishes no context length at all. Distinct from the no-store case below, which is what an
        // un-wired middleware sees.
        var store = new FakeToolCallCapabilityStore().SeedContextWindow(providerKey: "openai",
            modelName: "some-other-model", 128000, architecture: "gpt");

        var root = await ShowAsync(modelName: "gpt-5.4", resolver: ModelList(("gpt-5.4", "openai")), store: store);

        Assert.False(root.TryGetProperty(propertyName: "model_info", value: out _));
    }

    // The default-constructed middleware every existing test builds: both stores null. Behaviorally inert
    // is a documented promise of those parameters, so it gets its own assertion.
    [Fact]
    public async Task PostOllamaShow_WithNoStoresWired_OmitsModelInfo_ButStillDeclaresTools()
    {
        var root = await ShowAsync(modelName: "gpt-5.4", resolver: ModelList(("gpt-5.4", "openai")));

        Assert.False(root.TryGetProperty(propertyName: "model_info", value: out _));
        Assert.Equal(expectedSpan: ["completion", "tools"], actualArray: Capabilities(root));
    }

    [Fact]
    public async Task PostOllamaShow_RouterModel_UnionsCapabilities_AndTakesTheMaximumContextLength()
    {
        var store = new FakeToolCallCapabilityStore()
            .SeedContextWindow(providerKey: "local", modelName: "small-local", 8192, architecture: "qwen2")
            .SeedContextWindow(providerKey: "openai", modelName: "gpt-5.4", 128000, architecture: "gpt");

        var root = await ShowAsync(
            modelName: RouterModel,
            resolver: ModelList(("small-local", "local"), ("gpt-5.4", "openai")),
            store: store);

        Assert.Equal(expectedSpan: ["completion", "tools"], actualArray: Capabilities(root));

        var modelInfo = root.GetProperty("model_info");
        Assert.Equal(expected: "arcrouter", actual: modelInfo.GetProperty("general.architecture").GetString());
        Assert.Equal(128000, actual: modelInfo.GetProperty("arcrouter.context_length").GetInt32());
    }

    // The per-model governance gate (Enabled && PresentUpstream). A stopped model must not contribute its
    // window to the alias's maximum, or the alias advertises a window nothing behind it can serve.
    [Fact]
    public async Task PostOllamaShow_RouterModel_IgnoresADisabledModel()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelEntries(
            null,
            new ModelRouteEntry { ModelName = "small-local", Provider = "local", ProviderModelId = "small-local" },
            new ModelRouteEntry
            { ModelName = "big-local", Provider = "local", ProviderModelId = "big-local", Enabled = false });

        var store = new FakeToolCallCapabilityStore()
            .SeedContextWindow(providerKey: "local", modelName: "small-local", 8192, architecture: "qwen2")
            .SeedContextWindow(providerKey: "local", modelName: "big-local", 200000, architecture: "qwen2");

        var root = await ShowAsync(modelName: RouterModel, resolver: resolver, store: store);

        Assert.Equal(8192, actual: root.GetProperty("model_info").GetProperty("arcrouter.context_length").GetInt32());
    }

    // The provider gate, asserted separately from the model gate: they are distinct config surfaces and a
    // single test covering both would pass if either alone were dropped.
    [Fact]
    public async Task PostOllamaShow_RouterModel_IgnoresAModelOnADisabledProvider()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelEntries(
            disabledProviders: ["openai"],
            new ModelRouteEntry { ModelName = "small-local", Provider = "local", ProviderModelId = "small-local" },
            new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" });

        var store = new FakeToolCallCapabilityStore()
            .SeedContextWindow(providerKey: "local", modelName: "small-local", 8192, architecture: "qwen2")
            .SeedContextWindow(providerKey: "openai", modelName: "gpt-5.4", 128000, architecture: "gpt");

        var root = await ShowAsync(modelName: RouterModel, resolver: resolver, store: store);

        Assert.Equal(8192, actual: root.GetProperty("model_info").GetProperty("arcrouter.context_length").GetInt32());
    }

    // Honest rather than convenient: an alias backed by nothing routable can still complete, but cannot
    // promise tools, and correctly drops out of a filtering client's picker.
    [Fact]
    public async Task PostOllamaShow_RouterModel_WithNoEligibleModels_DeclaresCompletionOnly()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelEntries(
            null,
            new ModelRouteEntry
            {
                ModelName = "only-local",
                Provider = "local",
                ProviderModelId = "only-local",
                PresentUpstream = false
            });

        var root = await ShowAsync(modelName: RouterModel, resolver: resolver,
            store: new FakeToolCallCapabilityStore());

        Assert.Equal(expectedSpan: ["completion"], actualArray: Capabilities(root));
        Assert.False(root.TryGetProperty(propertyName: "model_info", value: out _));
    }

    // Pins the decision NOT to emit capabilities on /api/tags. Real Ollama publishes them only on
    // /api/show, and these handlers exist to be indistinguishable from it.
    [Fact]
    public async Task GetOllamaTags_DoesNotDeclareCapabilities()
    {
        var middleware = Middleware(resolver: ModelList(("gpt-5.4", "openai")),
            store: new FakeToolCallCapabilityStore());

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/tags";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        var root = ReadBody(context);
        foreach (var entry in root.GetProperty("models").EnumerateArray())
        {
            Assert.False(entry.TryGetProperty(propertyName: "capabilities", value: out _));
            Assert.False(entry.TryGetProperty(propertyName: "model_info", value: out _));
        }
    }

    private static IModelRouteResolver ModelList(params (string ModelName, string Provider)[] models)
    {
        return ModelRouteResolverTestFactory.CreateWithModelList(
            [.. models.Select(m => (m.ModelName, m.Provider, m.ModelName))]);
    }

    private static ProxyMiddleware Middleware(IModelRouteResolver resolver, FakeToolCallCapabilityStore? store)
    {
        var interceptor =
            new RequestInterceptor(logger: Mock.Of<ILogger<RequestInterceptor>>(), modelRouteResolver: resolver);
        var handler = new ThrowingHandler();

        return new ProxyMiddleware(
            logger: Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor: interceptor,
            httpClient: new HttpClient(handler),
            dependencies: new ProxyMiddlewareDependencies
            {
                CapabilityStore = store,
                ContextWindowStore = store
            }
        );
    }

    private static async Task<JsonElement> ShowAsync(
        string modelName, IModelRouteResolver resolver, FakeToolCallCapabilityStore? store = null)
    {
        var middleware = Middleware(resolver: resolver, store: store);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/show";
        var body = Encoding.UTF8.GetBytes($$"""{"model":"{{modelName}}"}""");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context: context, next: _ => Task.CompletedTask);

        Assert.Equal(expected: StatusCodes.Status200OK, actual: context.Response.StatusCode);
        return ReadBody(context);
    }

    private static string[] Capabilities(JsonElement root)
    {
        return [.. root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];
    }

    private static JsonElement ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(stream: context.Response.Body, encoding: Encoding.UTF8);
        return JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
    }

    /// <summary>Fails the test if the proxy forwards upstream; every path here must be answered locally.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "Upstream must never be called for a locally-answered Ollama endpoint.");
        }
    }
}