using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers what <c>POST /api/show</c> declares about a model: its <c>capabilities</c> array and its
/// <c>model_info</c> context window (<c>docs/router/ollama-show-capabilities-plan.md</c>).
///
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
        var root = await ShowAsync("gpt-5.4", ModelList(("gpt-5.4", "openai")));

        var capabilities = Capabilities(root);
        Assert.Equal(["completion", "tools"], capabilities);
    }

    // The judgment call recorded in ADR-0003: an `emulated` model cannot call tools natively - that row is
    // written precisely because its chat template renders none - but the router emulates them on its
    // behalf, and /api/show describes the endpoint the client is addressing. Declaring false here would
    // hide the router's own emulation feature from the one client that filters on this field.
    [Fact]
    public async Task PostOllamaShow_EmulatedModel_StillDeclaresTools()
    {
        var store = new FakeToolCallCapabilityStore().Seed(new ModelToolCapability(
            "local", "qwen-local", ToolCallDialectRegistry.Emulated.Name, DetectionConfidence.Template));

        var root = await ShowAsync("qwen-local", ModelList(("qwen-local", "local")), store);

        Assert.Contains("tools", Capabilities(root));
    }

    // The dominant state, and the one that decides whether the fix works at all: a fresh install has run no
    // scan, and no hosted provider can be probed. Withholding tools here would filter out exactly the cloud
    // models that unambiguously support them.
    [Fact]
    public async Task PostOllamaShow_UnclassifiedModel_StillDeclaresTools()
    {
        var root = await ShowAsync("gpt-5.4", ModelList(("gpt-5.4", "openai")), new FakeToolCallCapabilityStore());

        Assert.Contains("tools", Capabilities(root));
    }

    [Fact]
    public async Task PostOllamaShow_KnownContextLength_IsKeyedByTheProbedArchitecture()
    {
        var store = new FakeToolCallCapabilityStore().SeedContextWindow("local", "qwen-local", 32768, "qwen2");

        var root = await ShowAsync("qwen-local", ModelList(("qwen-local", "local")), store);

        var modelInfo = root.GetProperty("model_info");
        Assert.Equal("qwen2", modelInfo.GetProperty("general.architecture").GetString());
        Assert.Equal(32768, modelInfo.GetProperty("qwen2.context_length").GetInt32());
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
        var store = new FakeToolCallCapabilityStore().SeedContextWindow("openai", "some-other-model", 128000, "gpt");

        var root = await ShowAsync("gpt-5.4", ModelList(("gpt-5.4", "openai")), store);

        Assert.False(root.TryGetProperty("model_info", out _));
    }

    // The default-constructed middleware every existing test builds: both stores null. Behaviorally inert
    // is a documented promise of those parameters, so it gets its own assertion.
    [Fact]
    public async Task PostOllamaShow_WithNoStoresWired_OmitsModelInfo_ButStillDeclaresTools()
    {
        var root = await ShowAsync("gpt-5.4", ModelList(("gpt-5.4", "openai")));

        Assert.False(root.TryGetProperty("model_info", out _));
        Assert.Equal(["completion", "tools"], Capabilities(root));
    }

    [Fact]
    public async Task PostOllamaShow_RouterModel_UnionsCapabilities_AndTakesTheMaximumContextLength()
    {
        var store = new FakeToolCallCapabilityStore()
            .SeedContextWindow("local", "small-local", 8192, "qwen2")
            .SeedContextWindow("openai", "gpt-5.4", 128000, "gpt");

        var root = await ShowAsync(
            RouterModel,
            ModelList(("small-local", "local"), ("gpt-5.4", "openai")),
            store);

        Assert.Equal(["completion", "tools"], Capabilities(root));

        var modelInfo = root.GetProperty("model_info");
        Assert.Equal("arcrouter", modelInfo.GetProperty("general.architecture").GetString());
        Assert.Equal(128000, modelInfo.GetProperty("arcrouter.context_length").GetInt32());
    }

    // The per-model governance gate (Enabled && PresentUpstream). A stopped model must not contribute its
    // window to the alias's maximum, or the alias advertises a window nothing behind it can serve.
    [Fact]
    public async Task PostOllamaShow_RouterModel_IgnoresADisabledModel()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelEntries(
            disabledProviders: null,
            new ModelRouteEntry { ModelName = "small-local", Provider = "local", ProviderModelId = "small-local" },
            new ModelRouteEntry { ModelName = "big-local", Provider = "local", ProviderModelId = "big-local", Enabled = false });

        var store = new FakeToolCallCapabilityStore()
            .SeedContextWindow("local", "small-local", 8192, "qwen2")
            .SeedContextWindow("local", "big-local", 200000, "qwen2");

        var root = await ShowAsync(RouterModel, resolver, store);

        Assert.Equal(8192, root.GetProperty("model_info").GetProperty("arcrouter.context_length").GetInt32());
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
            .SeedContextWindow("local", "small-local", 8192, "qwen2")
            .SeedContextWindow("openai", "gpt-5.4", 128000, "gpt");

        var root = await ShowAsync(RouterModel, resolver, store);

        Assert.Equal(8192, root.GetProperty("model_info").GetProperty("arcrouter.context_length").GetInt32());
    }

    // Honest rather than convenient: an alias backed by nothing routable can still complete, but cannot
    // promise tools, and correctly drops out of a filtering client's picker.
    [Fact]
    public async Task PostOllamaShow_RouterModel_WithNoEligibleModels_DeclaresCompletionOnly()
    {
        var resolver = ModelRouteResolverTestFactory.CreateWithModelEntries(
            disabledProviders: null,
            new ModelRouteEntry { ModelName = "only-local", Provider = "local", ProviderModelId = "only-local", PresentUpstream = false });

        var root = await ShowAsync(RouterModel, resolver, new FakeToolCallCapabilityStore());

        Assert.Equal(["completion"], Capabilities(root));
        Assert.False(root.TryGetProperty("model_info", out _));
    }

    // Pins the decision NOT to emit capabilities on /api/tags. Real Ollama publishes them only on
    // /api/show, and these handlers exist to be indistinguishable from it.
    [Fact]
    public async Task GetOllamaTags_DoesNotDeclareCapabilities()
    {
        var middleware = Middleware(ModelList(("gpt-5.4", "openai")), new FakeToolCallCapabilityStore());

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/tags";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var root = ReadBody(context);
        foreach (var entry in root.GetProperty("models").EnumerateArray())
        {
            Assert.False(entry.TryGetProperty("capabilities", out _));
            Assert.False(entry.TryGetProperty("model_info", out _));
        }
    }

    private static IModelRouteResolver ModelList(params (string ModelName, string Provider)[] models) =>
        ModelRouteResolverTestFactory.CreateWithModelList(
            [.. models.Select(m => (m.ModelName, m.Provider, m.ModelName))]);

    private static ProxyMiddleware Middleware(IModelRouteResolver resolver, FakeToolCallCapabilityStore? store)
    {
        var interceptor = new RequestInterceptor(Mock.Of<ILogger<RequestInterceptor>>(), resolver);
        var handler = new ThrowingHandler();

        return new ProxyMiddleware(
            Mock.Of<ILogger<ProxyMiddleware>>(),
            interceptor,
            new HttpClient(handler),
            capabilityStore: store,
            contextWindowStore: store);
    }

    private static async Task<JsonElement> ShowAsync(
        string modelName, IModelRouteResolver resolver, FakeToolCallCapabilityStore? store = null)
    {
        var middleware = Middleware(resolver, store);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/show";
        var body = Encoding.UTF8.GetBytes($$"""{"model":"{{modelName}}"}""");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        return ReadBody(context);
    }

    private static string[] Capabilities(JsonElement root) =>
        [.. root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];

    private static JsonElement ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
    }

    /// <summary>Fails the test if the proxy forwards upstream; every path here must be answered locally.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Upstream must never be called for a locally-answered Ollama endpoint.");
    }
}
