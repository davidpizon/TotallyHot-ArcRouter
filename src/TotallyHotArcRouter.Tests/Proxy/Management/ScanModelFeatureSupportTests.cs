using Moq;
using System.Net;
using System.Runtime.CompilerServices;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using static TotallyHot.ArcRouter.Tests.Proxy.ModelFeatureSupportFixtures;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers how a provider scan records per-model capability records
/// (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c> Amendment 1): an Anthropic-shaped
/// endpoint's records are persisted, a later scan that cannot read the list keeps the previous records, an
/// endpoint that is not Anthropic-shaped is never asked for them, and one whose list proves it OpenAI-shaped has
/// any earlier records cleared.
/// </summary>
public sealed class ScanModelFeatureSupportTests : IDisposable
{
    private const string AnthropicListPage = $$"""
        {"data":[{"type":"model","id":"claude-haiku-4-5-20251001","capabilities":{{Haiku45Capabilities}}}],
         "has_more":false,"first_id":"claude-haiku-4-5-20251001","last_id":"claude-haiku-4-5-20251001"}
        """;

    private const string OpenAiBody = """{"object":"list","data":[{"id":"gpt-5.4"}]}""";

    private readonly TempDatabase _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    private static InMemoryProviderConfigStore StoreWithProvider(string key = "anthropic")
    {
        return new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = new() { BaseUrl = "https://api.anthropic.com" }
            }
        });
    }

    private static ManagementFacade Facade(IProviderConfigStore store, ToolCallCapabilityStore capabilities,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var scanner = new ProviderEndpointScanner(
            httpClient: new HttpClient(new DelegatingHandlerStub(request => Task.FromResult(respond(request)))),
            environment: Mock.Of<IEnvironmentVariableProvider>());

        return new ManagementFacade(store: store, environment: Mock.Of<IEnvironmentVariableProvider>(),
            httpClient: new HttpClient(),
            dependencies: new ManagementFacadeDependencies { EndpointScanner = scanner, CapabilityStore = capabilities });
    }

    private static HttpResponseMessage Ok(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private static bool IsModelList(HttpRequestMessage request)
    {
        return request.RequestUri!.AbsolutePath.EndsWith(value: "/v1/models", comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanningAnAnthropicEndpoint_PersistsEachModelsRecord()
    {
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var facade = Facade(store: StoreWithProvider(), capabilities: capabilities,
            respond: request => IsModelList(request) ? Ok(AnthropicListPage) : new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await facade.ScanCapabilitiesAsync(key: "anthropic",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Value!.AnthropicCompatible);
        var record = capabilities.GetModelFeatureSupport(providerKey: "anthropic", modelId: "claude-haiku-4-5-20251001");
        Assert.NotNull(record);
        Assert.False(record.IsSupported("effort"));
    }

    [Fact]
    public async Task ARescanThatCannotReadTheList_KeepsThePreviousRecords()
    {
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var listReadable = new StrongBox<bool>(true);
        var facade = Facade(store: StoreWithProvider(), capabilities: capabilities, respond: request =>
        {
            if (!IsModelList(request)) return new HttpResponseMessage(HttpStatusCode.NotFound);

            // The flavor probe (no query string) keeps answering, so the endpoint still reads as Anthropic-shaped;
            // only the paged capability read fails.
            return listReadable.Value || string.IsNullOrEmpty(request.RequestUri!.Query)
                ? Ok(AnthropicListPage)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await facade.ScanCapabilitiesAsync(key: "anthropic", cancellationToken: TestContext.Current.CancellationToken);
        listReadable.Value = false;
        await facade.ScanCapabilitiesAsync(key: "anthropic", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(capabilities.GetModelFeatureSupport(providerKey: "anthropic",
            modelId: "claude-haiku-4-5-20251001"));
    }

    [Fact]
    public async Task ARescanThatReachesNothing_KeepsThePreviousRecords()
    {
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var reachable = new StrongBox<bool>(true);
        var facade = Facade(store: StoreWithProvider(), capabilities: capabilities, respond: request =>
            reachable.Value && IsModelList(request)
                ? Ok(AnthropicListPage)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        await facade.ScanCapabilitiesAsync(key: "anthropic", cancellationToken: TestContext.Current.CancellationToken);
        reachable.Value = false;
        var rescan = await facade.ScanCapabilitiesAsync(key: "anthropic",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(rescan.Value!.ScanError);
        Assert.NotNull(capabilities.GetModelFeatureSupport(providerKey: "anthropic",
            modelId: "claude-haiku-4-5-20251001"));
    }

    [Fact]
    public async Task AProviderWhoseListTurnsOpenAiShaped_HasItsRecordsCleared()
    {
        // The same provider key re-pointed at an OpenAI-compatible endpoint: its list now proves there are no
        // capability records, so the old Anthropic ones must stop deciding what gets stripped.
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var anthropicShaped = new StrongBox<bool>(true);
        var facade = Facade(store: StoreWithProvider(), capabilities: capabilities, respond: request =>
            IsModelList(request)
                ? Ok(anthropicShaped.Value ? AnthropicListPage : OpenAiBody)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        await facade.ScanCapabilitiesAsync(key: "anthropic", cancellationToken: TestContext.Current.CancellationToken);
        anthropicShaped.Value = false;
        await facade.ScanCapabilitiesAsync(key: "anthropic", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(capabilities.GetModelFeatureSupport(providerKey: "anthropic", modelId: "claude-haiku-4-5-20251001"));
    }

    [Fact]
    public async Task AnEndpointThatIsNotAnthropicShaped_IsNeverAskedForRecords()
    {
        var capabilities = _temp.CreateToolCallCapabilityStore();
        var urls = new List<string>();
        var facade = Facade(store: StoreWithProvider("openai"), capabilities: capabilities, respond: request =>
        {
            urls.Add(request.RequestUri!.ToString());
            return IsModelList(request) ? Ok(OpenAiBody) : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await facade.ScanCapabilitiesAsync(key: "openai", cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(collection: urls, filter: url => url.Contains("limit="));
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
}
