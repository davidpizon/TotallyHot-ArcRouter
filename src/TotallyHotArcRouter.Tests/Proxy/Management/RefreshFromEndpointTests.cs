using Moq;
using System.Net;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementFacade.RefreshFromEndpointAsync"/>: the consolidated "Refresh from endpoint"
/// operation that discovers a provider's live model list and reconciles it into
/// <see cref="ModelRoutingOptions.ModelList"/> - the router noticing added/removed models itself, rather
/// than the GUI orchestrating separate discovery and capability-scan calls.
/// </summary>
public sealed class RefreshFromEndpointTests : IDisposable
{
    private const string OpenAiBody = """{"object":"list","data":[{"id":"gpt-5.4"},{"id":"gpt-5.4-mini"}]}""";

    private readonly TempDatabase _temp = new();

    private static InMemoryProviderConfigStore StoreWithProvider(
        string key = "lmstudio", List<ModelRouteEntry>? models = null) =>
        new(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = new ProviderOptions { BaseUrl = "http://localhost:1234/v1" },
            },
            ModelList = models ?? [],
        });

    private ToolCallCapabilityStore CapabilityStore() => _temp.CreateToolCallCapabilityStore();

    private static ProviderEndpointScanner Scanner(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
        new(new HttpClient(new DelegatingHandlerStub(handler)), Mock.Of<IEnvironmentVariableProvider>());

    private static ProviderEndpointScanner AlwaysOk(string body) =>
        Scanner(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));

    private static ProviderEndpointScanner AlwaysNotFound() =>
        Scanner(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

    // Discovery itself goes through ManagementFacade's own injected HttpClient (DiscoverModelsCoreAsync),
    // not the endpoint scanner - the two probe different things and are wired independently.
    private static ManagementFacade Facade(
        IProviderConfigStore store,
        HttpMessageHandler discoveryHandler,
        ProviderEndpointScanner? scanner = null,
        ToolCallCapabilityStore? capabilityStore = null,
        IProviderInteractionStatusStore? interactionStatusStore = null) =>
        new(store, Mock.Of<IEnvironmentVariableProvider>(), new HttpClient(discoveryHandler),
            new ManagementFacadeDependencies
            {
                EndpointScanner = scanner,
                CapabilityStore = capabilityStore,
                InteractionStatusStore = interactionStatusStore,
            });

    private static DelegatingHandlerStub DiscoveryHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
        new(handler);

    private static DelegatingHandlerStub DiscoveryOk(string body) =>
        DiscoveryHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));

    private static DelegatingHandlerStub DiscoveryUnsupported() =>
        DiscoveryHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

    [Fact]
    public async Task RefreshFromEndpoint_ForAnUnknownProvider_IsNotFound()
    {
        var facade = Facade(StoreWithProvider(), DiscoveryOk(OpenAiBody));

        var result = await facade.RefreshFromEndpointAsync("nope", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.NotFound, result.ErrorType);
    }

    [Fact]
    public async Task RefreshFromEndpoint_AddsANewlyDiscoveredModel_AsDisabled()
    {
        var store = StoreWithProvider();
        var facade = Facade(store, DiscoveryOk(OpenAiBody));

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var provider = Assert.Single(result.Value!.Providers);
        Assert.Equal(2, provider.Models.Count);
        Assert.All(provider.Models, m =>
        {
            // Auto-added, never auto-started: the router notices the model exists, the operator decides
            // whether it should receive traffic.
            Assert.False(m.Enabled);
            Assert.True(m.PresentUpstream);
        });
        Assert.Contains(provider.Models, m => m.ModelName == "gpt-5.4");
        Assert.Contains(provider.Models, m => m.ModelName == "gpt-5.4-mini");
    }

    [Fact]
    public async Task RefreshFromEndpoint_FlagsAMissingConfiguredModel_AsNotPresentUpstream_ButPreservesEnabled()
    {
        var store = StoreWithProvider(models:
        [
            new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "lmstudio", ProviderModelId = "gpt-5.4", Enabled = true },
            new ModelRouteEntry { ModelName = "unloaded-model", Provider = "lmstudio", ProviderModelId = "unloaded-model", Enabled = true },
        ]);
        // The endpoint only reports "gpt-5.4" now - "unloaded-model" is configured but absent this time.
        var facade = Facade(store, DiscoveryOk("""{"object":"list","data":[{"id":"gpt-5.4"}]}"""));

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var models = Assert.Single(result.Value!.Providers).Models;
        var stillPresent = models.Single(m => m.ModelName == "gpt-5.4");
        Assert.True(stillPresent.PresentUpstream);

        var nowMissing = models.Single(m => m.ModelName == "unloaded-model");
        Assert.False(nowMissing.PresentUpstream);
        // Never deleted, and its own Enabled state survives untouched - only PresentUpstream changed.
        Assert.True(nowMissing.Enabled);
    }

    [Fact]
    public async Task RefreshFromEndpoint_RediscoveringAModel_ClearsPresentUpstream_WithoutTouchingEnabled()
    {
        // A model previously flagged absent, and which the operator had explicitly started, resumes
        // routable the moment it's rediscovered - no extra click needed.
        var store = StoreWithProvider(models:
        [
            new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "lmstudio", ProviderModelId = "gpt-5.4", Enabled = true, PresentUpstream = false },
        ]);
        var facade = Facade(store, DiscoveryOk("""{"object":"list","data":[{"id":"gpt-5.4"}]}"""));

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var model = Assert.Single(Assert.Single(result.Value!.Providers).Models);
        Assert.True(model.PresentUpstream);
        Assert.True(model.Enabled);
    }

    [Fact]
    public async Task RefreshFromEndpoint_WhenDiscoveryIsUnsupported_LeavesExistingModelsCompletelyUntouched()
    {
        // A provider that doesn't answer OpenAI-shaped /v1/models (hosted Anthropic, Bedrock) must not have
        // its existing models mass-flagged absent just because the question couldn't be asked.
        var store = StoreWithProvider(models:
        [
            new ModelRouteEntry { ModelName = "claude-opus", Provider = "lmstudio", ProviderModelId = "claude-opus", Enabled = true, PresentUpstream = true },
        ]);
        var facade = Facade(store, DiscoveryUnsupported());

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var model = Assert.Single(Assert.Single(result.Value!.Providers).Models);
        Assert.True(model.PresentUpstream);
        Assert.True(model.Enabled);
    }

    [Fact]
    public async Task RefreshFromEndpoint_WhenNothingChanged_IsANoOp_AndDoesNotBumpTheConfigVersion()
    {
        var store = StoreWithProvider(models:
        [
            new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "lmstudio", ProviderModelId = "gpt-5.4", Enabled = true, PresentUpstream = true },
        ]);
        var facade = Facade(store, DiscoveryOk("""{"object":"list","data":[{"id":"gpt-5.4"}]}"""));
        var versionBefore = store.Snapshot.Version;

        await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.Equal(versionBefore, store.Snapshot.Version);
    }

    [Fact]
    public async Task RefreshFromEndpoint_AlsoScansEndpointCapabilitiesAndDetectsDialects()
    {
        var store = StoreWithProvider();
        var capabilities = CapabilityStore();
        var facade = Facade(store, DiscoveryOk(OpenAiBody), AlwaysOk(OpenAiBody), capabilities);

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var provider = Assert.Single(result.Value!.Providers);
        Assert.NotNull(provider.EndpointCapabilities);
        Assert.True(provider.EndpointCapabilities!.OpenAiCompatible);
    }

    [Fact]
    public async Task RefreshFromEndpoint_WithNoCapabilityStoreWired_StillReconcilesModels()
    {
        // Model reconciliation is this method's primary duty and must not become unavailable just because
        // the endpoint-flavor scan/dialect detection can't run - unlike ScanCapabilitiesAsync, which fails
        // outright without a capability store.
        var store = StoreWithProvider();
        var facade = Facade(store, DiscoveryOk(OpenAiBody));

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var provider = Assert.Single(result.Value!.Providers);
        Assert.Equal(2, provider.Models.Count);
        Assert.Null(provider.EndpointCapabilities);
    }

    [Fact]
    public async Task RefreshFromEndpoint_UnsupportedScanner_StillReconcilesModels_ButSkipsEndpointFlavors()
    {
        var store = StoreWithProvider();
        var capabilities = CapabilityStore();
        var facade = Facade(store, DiscoveryOk(OpenAiBody), AlwaysNotFound(), capabilities);

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var provider = Assert.Single(result.Value!.Providers);
        Assert.Equal(2, provider.Models.Count);
        // The scanner ran (it's wired up) and reported nothing recognized - a real, persisted result, not a
        // null "never scanned" state.
        Assert.NotNull(provider.EndpointCapabilities);
        Assert.False(provider.EndpointCapabilities!.OpenAiCompatible);
    }

    // ----- DiscoverModelsAsync also records an interaction outcome -----

    [Fact]
    public async Task DiscoverModels_OnSuccess_RecordsASuccessfulInteraction()
    {
        var interactionStatus = new ProviderInteractionStatusStore();
        var facade = Facade(StoreWithProvider(), DiscoveryOk(OpenAiBody), interactionStatusStore: interactionStatus);

        await facade.DiscoverModelsAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(interactionStatus.Get("lmstudio")!.Ok);
    }

    [Fact]
    public async Task DiscoverModels_WhenUnsupported_RecordsAFailure()
    {
        // Discovery itself has no capability scan to corroborate against - it always records a discovery
        // Error as a failure, unlike RefreshFromEndpointAsync's more forgiving combined rule.
        var interactionStatus = new ProviderInteractionStatusStore();
        var facade = Facade(StoreWithProvider(), DiscoveryUnsupported(), interactionStatusStore: interactionStatus);

        await facade.DiscoverModelsAsync("lmstudio", TestContext.Current.CancellationToken);

        var status = interactionStatus.Get("lmstudio");
        Assert.NotNull(status);
        Assert.False(status!.Ok);
        Assert.Equal("Discover models", status.Operation);
    }

    // ----- AdminAction (the GUI's warning-icon/toast source) -----

    [Fact]
    public async Task RefreshFromEndpoint_OnSuccess_RecordsAndSurfacesASuccessfulInteraction()
    {
        var interactionStatus = new ProviderInteractionStatusStore();
        var facade = Facade(StoreWithProvider(), DiscoveryOk(OpenAiBody), interactionStatusStore: interactionStatus);

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        var provider = Assert.Single(result.Value!.Providers);
        Assert.NotNull(provider.AdminAction);
        Assert.True(provider.AdminAction!.Ok);
        Assert.Equal("Refresh from endpoint", provider.AdminAction.Operation);
        Assert.True(interactionStatus.Get("lmstudio")!.Ok);
    }

    [Fact]
    public async Task RefreshFromEndpoint_WhenDiscoveryFailsAndNoScanCorroboratesIt_RecordsAFailure()
    {
        // The motivating bug: an expired/invalid API key. Discovery reports a 401 and no capability scan is
        // wired up to corroborate it either way, so the failure must be recorded rather than silently
        // dropped - which is exactly what happened before this store existed.
        var discoveryUnauthorized = DiscoveryHandler(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var interactionStatus = new ProviderInteractionStatusStore();
        var facade = Facade(StoreWithProvider(), discoveryUnauthorized, interactionStatusStore: interactionStatus);

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        var provider = Assert.Single(result.Value!.Providers);
        Assert.NotNull(provider.AdminAction);
        Assert.False(provider.AdminAction!.Ok);
        Assert.Contains("401", provider.AdminAction.Message);
    }

    [Fact]
    public async Task RefreshFromEndpoint_WhenDiscoveryIsUnsupportedButTheScanDetectsAFlavor_RecordsSuccess()
    {
        // A healthy provider with no OpenAI-shaped /v1/models (hosted Anthropic, Bedrock) always reports a
        // discovery Error - that alone must not read as a failure once the capability scan corroborates the
        // endpoint is reachable and authenticating.
        var capabilities = CapabilityStore();
        var interactionStatus = new ProviderInteractionStatusStore();
        var facade = Facade(
            StoreWithProvider(),
            DiscoveryUnsupported(),
            AlwaysOk(OpenAiBody),
            capabilities,
            interactionStatus);

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        var provider = Assert.Single(result.Value!.Providers);
        Assert.NotNull(provider.AdminAction);
        Assert.True(provider.AdminAction!.Ok);
    }

    [Fact]
    public async Task RefreshFromEndpoint_WhenDiscoveryFailsAndTheScanAlsoDetectsNothing_RecordsAFailure()
    {
        var capabilities = CapabilityStore();
        var interactionStatus = new ProviderInteractionStatusStore();
        var facade = Facade(
            StoreWithProvider(),
            DiscoveryUnsupported(),
            AlwaysNotFound(),
            capabilities,
            interactionStatus);

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        var provider = Assert.Single(result.Value!.Providers);
        Assert.NotNull(provider.AdminAction);
        Assert.False(provider.AdminAction!.Ok);
    }

    [Fact]
    public async Task RefreshFromEndpoint_ANewlySuccessfulRefresh_ClearsAPriorFailure()
    {
        var interactionStatus = new ProviderInteractionStatusStore();
        interactionStatus.RecordFailure("lmstudio", "Refresh from endpoint", "stale failure");
        var facade = Facade(StoreWithProvider(), DiscoveryOk(OpenAiBody), interactionStatusStore: interactionStatus);

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(result.Value!.Providers).AdminAction!.Ok);
    }

    [Fact]
    public async Task RefreshFromEndpoint_WithoutAnInteractionStatusStoreWired_StillSucceeds()
    {
        // ManagementFacade is constructible without this store too (ProxyServer builds its own instance) -
        // BuildProvidersResponse must degrade to a null AdminAction rather than throw.
        var facade = Facade(StoreWithProvider(), DiscoveryOk(OpenAiBody));

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(Assert.Single(result.Value!.Providers).AdminAction);
    }

    [Fact]
    public async Task RefreshFromEndpoint_OnSuccess_DoesNotTouchTheLiveTrafficTrack()
    {
        // docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-tab.md's track
        // separation: an admin action must never write, clear, or otherwise affect LiveTraffic - it is
        // written only from the hot request path.
        var interactionStatus = new ProviderInteractionStatusStore();
        interactionStatus.RecordLiveTrafficFailure("lmstudio", ProviderInteractionKind.OutOfCredits, "out of credits");
        var facade = Facade(StoreWithProvider(), DiscoveryOk(OpenAiBody), interactionStatusStore: interactionStatus);

        var result = await facade.RefreshFromEndpointAsync("lmstudio", TestContext.Current.CancellationToken);

        var provider = Assert.Single(result.Value!.Providers);
        Assert.True(provider.AdminAction!.Ok);
        Assert.False(provider.LiveTraffic!.Ok);
        Assert.Equal("out of credits", provider.LiveTraffic.Message);
    }

    public void Dispose() => _temp.Dispose();

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}

