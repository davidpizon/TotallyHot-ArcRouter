using System.Net;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using TotallyHot.ArcRouter.Tests.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers how <see cref="ManagementFacade"/> drives the endpoint-capability scan
/// (<c>docs/router/tool-call-normalization.md</c> Phase 2): the explicit
/// <c>POST /admin/providers/{key}/scan-capabilities</c> path, and the best-effort refresh that runs after a
/// provider save.
///
/// <para>
/// The load-bearing guarantee is that the save-time scan can never affect the save. A provider that is
/// simply not running yet must still be configurable - which is the normal case when adding a local
/// LM Studio or Ollama entry before starting the server.
/// </para>
/// </summary>
public sealed class ScanCapabilitiesTests : IDisposable
{
    private const string OpenAiBody = """{"object":"list","data":[{"id":"gpt-5.4"}]}""";

    private readonly TempDatabase _temp = new();

    private static InMemoryProviderConfigStore StoreWithProvider(string key = "lmstudio") =>
        new(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = new ProviderOptions { BaseUrl = "http://localhost:1234/v1" },
            },
        });

    private ToolCallCapabilityStore CapabilityStore() => _temp.CreateToolCallCapabilityStore();

    private static ProviderEndpointScanner Scanner(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
        new(new HttpClient(new DelegatingHandlerStub(handler)), Mock.Of<IEnvironmentVariableProvider>());

    private static ProviderEndpointScanner AlwaysOk(string body) =>
        Scanner(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));

    private static ProviderEndpointScanner AlwaysThrows() =>
        Scanner(_ => throw new HttpRequestException("connection refused"));

    private static ManagementFacade Facade(
        IProviderConfigStore store,
        ProviderEndpointScanner? scanner = null,
        ToolCallCapabilityStore? capabilityStore = null) =>
        new(store, Mock.Of<IEnvironmentVariableProvider>(), new HttpClient(),
            new ManagementFacadeDependencies { EndpointScanner = scanner, CapabilityStore = capabilityStore });

    // ----- The explicit scan endpoint -----

    [Fact]
    public async Task ScanCapabilities_PersistsWhatTheProviderAnswered()
    {
        var capabilities = CapabilityStore();
        var facade = Facade(StoreWithProvider(), AlwaysOk(OpenAiBody), capabilities);

        var result = await facade.ScanCapabilitiesAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Value!.OpenAiCompatible);
        Assert.True(capabilities.GetProviderCapabilities("lmstudio")!.OpenAiCompatible);
    }

    [Fact]
    public async Task ScanCapabilities_IsSurfacedThroughListProviders()
    {
        // ManagementFacade.BuildProvidersResponse - the projection every read and every mutation's success
        // path returns through - reads the capability store fresh each call, so a scan's effect must be
        // visible there too, not just via the narrower GetProviderCapabilities the test above checks.
        var capabilities = CapabilityStore();
        var facade = Facade(StoreWithProvider(), AlwaysOk(OpenAiBody), capabilities);

        await facade.ScanCapabilitiesAsync("lmstudio", TestContext.Current.CancellationToken);

        var provider = Assert.Single(facade.ListProviders().Providers);
        Assert.NotNull(provider.EndpointCapabilities);
        Assert.True(provider.EndpointCapabilities!.OpenAiCompatible);
    }

    [Fact]
    public void ListProviders_BeforeAnyScanHasRun_LeavesEndpointCapabilitiesNull()
    {
        var facade = Facade(StoreWithProvider(), AlwaysOk(OpenAiBody), CapabilityStore());

        var provider = Assert.Single(facade.ListProviders().Providers);

        Assert.Null(provider.EndpointCapabilities);
    }

    [Fact]
    public void ListProviders_SurfacesADetectedModelDialectAndConfidence()
    {
        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["lmstudio"] = new ProviderOptions { BaseUrl = "http://localhost:1234/v1" },
            },
            ModelList = [new ModelRouteEntry { ModelName = "qwen2.5-coder", Provider = "lmstudio", ProviderModelId = "qwen2.5-coder" }],
        });
        var capabilities = CapabilityStore();
        capabilities.TryRecordModelCapability(new ModelToolCapability(
            "lmstudio", "qwen2.5-coder", "hermes", DetectionConfidence.Observed, "test evidence"));
        var facade = Facade(store, AlwaysOk(OpenAiBody), capabilities);

        var model = Assert.Single(Assert.Single(facade.ListProviders().Providers).Models);

        Assert.Equal("hermes", model.Dialect);
        Assert.Equal("Observed", model.Confidence);
    }

    [Fact]
    public void ListProviders_WithNoCapabilityStore_LeavesDialectAndCapabilitiesNull()
    {
        // ManagementFacade is constructible without the scanning dependencies (ProxyServer builds its own
        // instance) - BuildProvidersResponse must degrade to null fields rather than throw.
        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["lmstudio"] = new ProviderOptions { BaseUrl = "http://localhost:1234/v1" },
            },
            ModelList = [new ModelRouteEntry { ModelName = "qwen2.5-coder", Provider = "lmstudio", ProviderModelId = "qwen2.5-coder" }],
        });
        var facade = Facade(store);

        var provider = Assert.Single(facade.ListProviders().Providers);
        var model = Assert.Single(provider.Models);

        Assert.Null(provider.EndpointCapabilities);
        Assert.Null(model.Dialect);
        Assert.Null(model.Confidence);
    }

    [Fact]
    public async Task ScanCapabilities_ForAnUnknownProvider_IsNotFound()
    {
        var facade = Facade(StoreWithProvider(), AlwaysOk(OpenAiBody), CapabilityStore());

        var result = await facade.ScanCapabilitiesAsync("nope", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.NotFound, result.ErrorType);
    }

    [Fact]
    public async Task ScanCapabilities_WithoutAScanner_ReportsUnavailable()
    {
        // The facade is constructible without the scanning dependencies (ProxyServer builds its own
        // instance), so this must degrade rather than throw.
        var facade = Facade(StoreWithProvider());

        var result = await facade.ScanCapabilitiesAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public async Task ScanCapabilities_ReportsAFailedScanToTheCaller()
    {
        // Unlike the save-time scan, an explicit scan's failure is surfaced - the operator asked for it, so
        // they should see why it did not work.
        var capabilities = CapabilityStore();
        var facade = Facade(StoreWithProvider(), AlwaysThrows(), capabilities);

        var result = await facade.ScanCapabilitiesAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success); // the scan itself completed; it reports the failure in its payload
        Assert.False(result.Value!.OpenAiCompatible);
        Assert.Contains("connection refused", result.Value.ScanError);
    }

    [Fact]
    public async Task ScanCapabilities_WhenItsBudgetElapses_YieldsAResultRatherThanThrowing()
    {
        // ScanCapabilitiesAsync now runs under its own 15s budget, because the probes are sequential and
        // the injected HttpClient uses the default 100s timeout - an upstream that accepts connections and
        // never responds would otherwise hang this admin endpoint for roughly three times that.
        //
        // The 15s budget itself is not worth waiting out in a unit test. What matters, and what is asserted
        // here, is the behavior that makes bounding it safe: when the budget elapses mid-probe, HttpClient
        // surfaces it as TaskCanceledException, and ScanAsync must report that in its payload rather than
        // let it escape into the admin endpoint as a 500.
        var timesOut = Scanner(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));
        var facade = Facade(StoreWithProvider(), timesOut, CapabilityStore());

        var result = await facade.ScanCapabilitiesAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.Value!.OpenAiCompatible);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ScanError));
    }

    [Fact]
    public async Task ScanCapabilities_WhenTheCallerAborts_LeavesAPreviouslyGoodRecordIntact()
    {
        // The scanner is deliberately fail-open: it reports cancellation as an all-false payload with a
        // ScanError rather than throwing. That is right for the scanner, but it means a client disconnect
        // would otherwise persist a result no probe ever established - durably degrading a healthy
        // provider's record to "answers nothing" just because the operator navigated away mid-scan.
        var capabilities = CapabilityStore();
        var healthy = Facade(StoreWithProvider(), AlwaysOk(OpenAiBody), capabilities);
        await healthy.ScanCapabilitiesAsync("lmstudio", TestContext.Current.CancellationToken);
        Assert.True(capabilities.GetProviderCapabilities("lmstudio")!.OpenAiCompatible);

        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var facade = Facade(StoreWithProvider(), AlwaysThrows(), capabilities);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => facade.ScanCapabilitiesAsync("lmstudio", aborted.Token));

        Assert.True(capabilities.GetProviderCapabilities("lmstudio")!.OpenAiCompatible);
    }

    [Fact]
    public async Task ScanCapabilities_WhenOnlyTheBudgetElapses_StillRecordsTheFailure()
    {
        // The counterpart to the test above, and the reason the guard keys on the caller's token rather than
        // on the scan payload carrying an error. A provider that does not respond within the budget is a
        // real observation about the provider and must still be recorded - otherwise the guard would
        // suppress precisely the failures an operator runs an explicit scan to discover.
        var capabilities = CapabilityStore();
        var timesOut = Scanner(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));
        var facade = Facade(StoreWithProvider(), timesOut, capabilities);

        var result = await facade.ScanCapabilitiesAsync("lmstudio", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var stored = capabilities.GetProviderCapabilities("lmstudio");
        Assert.NotNull(stored);
        Assert.False(stored!.OpenAiCompatible);
        Assert.False(string.IsNullOrWhiteSpace(stored.ScanError));
    }

    // ----- The best-effort scan on provider save -----

    [Fact]
    public async Task UpsertProvider_RefreshesTheCapabilityRecord()
    {
        var store = StoreWithProvider();
        var capabilities = CapabilityStore();
        var facade = Facade(store, AlwaysOk(OpenAiBody), capabilities);

        await facade.UpsertProviderAsync(
            "lmstudio",
            new ProviderWriteRequest("http://localhost:1234/v1", null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.True(capabilities.GetProviderCapabilities("lmstudio")!.OpenAiCompatible);
    }

    [Fact]
    public async Task UpsertProvider_StillSucceeds_WhenTheScanFails()
    {
        // The case that matters: adding a local provider before starting the server it points at.
        var store = StoreWithProvider();
        var facade = Facade(store, AlwaysThrows(), CapabilityStore());

        var result = await facade.UpsertProviderAsync(
            "lmstudio",
            new ProviderWriteRequest("http://localhost:1234/v1", null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("http://localhost:1234/v1", store.Snapshot.Options.Providers["lmstudio"].BaseUrl);
    }

    [Fact]
    public async Task UpsertProvider_StillSucceeds_WhenNoScannerIsConfigured()
    {
        var store = StoreWithProvider();
        var facade = Facade(store);

        var result = await facade.UpsertProviderAsync(
            "lmstudio",
            new ProviderWriteRequest("https://changed.invalid", null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("https://changed.invalid", store.Snapshot.Options.Providers["lmstudio"].BaseUrl);
    }

    [Fact]
    public async Task UpsertProvider_StillSucceeds_WhenTheScannerThrowsSomethingUnexpected()
    {
        // TryScanCapabilitiesAsync swallows everything, not just HTTP-shaped failures - a save that has
        // already been persisted must not be reported as failed.
        var store = StoreWithProvider();
        var facade = Facade(
            store,
            Scanner(_ => throw new InvalidOperationException("boom")),
            CapabilityStore());

        var result = await facade.UpsertProviderAsync(
            "lmstudio",
            new ProviderWriteRequest("https://changed.invalid", null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
    }

    public void Dispose() => _temp.Dispose();

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}

