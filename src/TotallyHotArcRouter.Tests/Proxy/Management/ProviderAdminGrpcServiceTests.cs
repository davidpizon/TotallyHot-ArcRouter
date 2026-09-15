using Grpc.Core;
using Grpc.Core.Testing;
using Moq;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ProviderAdminGrpcService"/>: request-to-facade translation, presence-sensitive
/// message fields (a request whose optional message is entirely omitted must reject with
/// <see cref="StatusCode.InvalidArgument"/> rather than null-referencing), and
/// <see cref="ManagementResult{T}"/>-to-<see cref="RpcException"/> status mapping. Constructs
/// <see cref="ManagementFacade"/> directly against an in-memory store, mirroring
/// <see cref="ManagementFacadeTests"/>'s own setup, rather than booting a full <see cref="ProxyServer"/> -
/// this is a unit test of the transport-translation layer, not an integration test of the HTTP/gRPC hosts.
/// </summary>
public sealed class ProviderAdminGrpcServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static ProviderAdminGrpcService CreateService(ModelRoutingOptions? options = null,
        ManagementFacadeDependencies? dependencies = null, HttpClient? httpClient = null)
    {
        var store = new InMemoryProviderConfigStore(options ?? SeedOptions());
        var facade = new ManagementFacade(store: store, environment: Mock.Of<IEnvironmentVariableProvider>(),
            httpClient: httpClient ?? new HttpClient(), dependencies: dependencies);
        return new ProviderAdminGrpcService(facade);
    }

    /// <summary>Always answers the same OpenAI-shaped model-list body, regardless of the requested path.</summary>
    private static HttpClient AlwaysOkModelList()
    {
        const string body = """{"object":"list","data":[{"id":"gpt-5.4"}]}""";
        return new HttpClient(new DelegatingHandlerStub(_ =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) })));
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

    private static ServerCallContext CreateContext()
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: [],
            cancellationToken: Ct,
            peer: "test-peer",
            authContext: null!,
            null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => null,
            writeOptionsSetter: _ => { });
    }

    [Fact]
    public async Task ListProviders_ReturnsConfiguredProviders()
    {
        var service = CreateService();

        var response = await service.ListProviders(new Contract.ListProvidersRequest(), CreateContext());

        var provider = Assert.Single(response.Providers);
        Assert.Equal(expected: "openai", actual: provider.Key);
        Assert.Equal(expected: "https://api.openai.com", actual: provider.BaseUrl);
        var model = Assert.Single(provider.Models);
        Assert.Equal(expected: "gpt-5.4", actual: model.ModelName);
    }

    [Fact]
    public async Task RemoveProvider_UnknownKey_ThrowsNotFound()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.RemoveProvider(new Contract.RemoveProviderRequest { Key = "unknown" }, CreateContext()));

        Assert.Equal(expected: StatusCode.NotFound, actual: ex.StatusCode);
    }

    [Fact]
    public async Task SetProviderBudget_MissingBudgetField_ThrowsInvalidArgument()
    {
        // Budget is a message field: a client that omits it entirely leaves request.Budget null, which must
        // not reach ManagementFacade.SetBudget as a null-reference.
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.SetProviderBudget(
            new Contract.SetProviderBudgetRequest { ProviderKey = "openai" }, CreateContext()));

        Assert.Equal(expected: StatusCode.InvalidArgument, actual: ex.StatusCode);
    }

    [Fact]
    public async Task SetProviderBudget_WithCaps_UpdatesAndReturnsRefreshedList()
    {
        using var temp = new TempDatabase();
        var service = CreateService(dependencies: new ManagementFacadeDependencies
        {
            BudgetStore = temp.CreateBudgetStore()
        });

        var response = await service.SetProviderBudget(new Contract.SetProviderBudgetRequest
        {
            ProviderKey = "openai",
            Budget = new Contract.BudgetWrite { DollarCap = "50.00" }
        }, CreateContext());

        var provider = Assert.Single(response.Providers);
        Assert.Equal(expected: "50.00", actual: provider.DollarCap);
    }

    [Fact]
    public async Task UpsertModel_MissingModelField_ThrowsInvalidArgument()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.UpsertModel(
            new Contract.UpsertModelRequest { ProviderKey = "openai", ModelName = "new-model" }, CreateContext()));

        Assert.Equal(expected: StatusCode.InvalidArgument, actual: ex.StatusCode);
    }

    [Fact]
    public async Task SetPriceOverride_MissingOverrideField_ThrowsInvalidArgument()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.SetPriceOverride(new Contract.SetPriceOverrideRequest(), CreateContext()));

        Assert.Equal(expected: StatusCode.InvalidArgument, actual: ex.StatusCode);
    }

    [Fact]
    public async Task GetRateLimitHistory_HoursOmitted_DefaultsToSixHoursRatherThanClamping()
    {
        // hours is `optional double`: an omitted field must behave like the REST-era client's 6-hour
        // default, not clamp to the facade's 0.25-hour floor as a bare proto3 scalar's zero value would.
        // Observed indirectly: a repository seeded 1 hour back is inside a 6-hour window but would fall
        // outside a 0.25-hour (15-minute) one.
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        repository.UpsertRateLimitHeaders(providerKey: "openai",
            headers: [new RateLimitHeaderRow(HeaderName: "x-ratelimit-remaining-requests", HeaderValue: "100")],
            observedAtUtc: DateTimeOffset.UtcNow.AddHours(-1));
        var service = CreateService(dependencies: new ManagementFacadeDependencies { RateLimitRepository = repository });

        var result = await service.GetRateLimitHistory(
            new Contract.GetRateLimitHistoryRequest { ProviderKey = "openai" }, CreateContext());

        Assert.NotEmpty(result.Dimensions);
    }

    [Fact]
    public async Task GetRateLimitHistory_UnknownProvider_ThrowsNotFound()
    {
        using var temp = new TempDatabase();
        var service = CreateService(dependencies: new ManagementFacadeDependencies
        {
            RateLimitRepository = temp.CreateRateLimitRepository()
        });

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.GetRateLimitHistory(
            new Contract.GetRateLimitHistoryRequest { ProviderKey = "unknown" }, CreateContext()));

        Assert.Equal(expected: StatusCode.NotFound, actual: ex.StatusCode);
    }

    [Fact]
    public async Task SetProviderEnabled_TogglesAndReturnsRefreshedList()
    {
        var service = CreateService();

        var response = await service.SetProviderEnabled(
            new Contract.SetProviderEnabledRequest { Key = "openai", Enabled = false }, CreateContext());

        var provider = Assert.Single(response.Providers);
        Assert.False(provider.Enabled);
    }

    [Fact]
    public async Task DiscoverModels_UnknownProvider_ThrowsNotFound()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.DiscoverModels(
            new Contract.DiscoverModelsRequest { ProviderKey = "unknown" }, CreateContext()));

        Assert.Equal(expected: StatusCode.NotFound, actual: ex.StatusCode);
    }

    // Web GUI migration plan Phase P2: these five cover the RPC wire-mapping this class itself performs
    // (request -> facade call -> ManagementResult -> wire response) for the methods that used to be
    // exercised only indirectly, through the now-deleted REST /admin/* integration tests hitting the
    // same underlying ManagementFacade. ManagementFacade's own business-logic edge cases (masking,
    // cascading, budget math) stay covered by ManagementFacadeTests/SetBudgetTests/
    // ProviderOptionsPreservationTests et al., which this class only delegates to.

    [Fact]
    public async Task UpsertProvider_UpdatesBaseUrlAndReplacesHeaders_ReturnsRefreshedList()
    {
        var service = CreateService();
        var request = new Contract.UpsertProviderRequest
        {
            Key = "openai",
            BaseUrl = "https://api.openai.example",
            ReplaceHeaders = true
        };
        request.Headers.Add(new Contract.HeaderWrite { Name = "Authorization", Value = "sk-test" });

        var response = await service.UpsertProvider(request, CreateContext());

        var provider = Assert.Single(response.Providers);
        Assert.Equal(expected: "https://api.openai.example", actual: provider.BaseUrl);
        var header = Assert.Single(provider.Headers);
        Assert.Equal(expected: "Authorization", actual: header.Name);
    }

    [Fact]
    public async Task UpsertModel_AddsModel_ReturnsRefreshedList()
    {
        var service = CreateService();
        var request = new Contract.UpsertModelRequest
        {
            ProviderKey = "openai",
            ModelName = "gpt-6",
            Model = new Contract.ModelWrite { ProviderModelId = "gpt-6-preview" }
        };

        var response = await service.UpsertModel(request, CreateContext());

        var provider = Assert.Single(response.Providers);
        Assert.Contains(provider.Models, m => m.ModelName == "gpt-6" && m.ProviderModelId == "gpt-6-preview");
    }

    [Fact]
    public async Task RemoveModel_RemovesModel_ReturnsRefreshedList()
    {
        var service = CreateService();

        var response = await service.RemoveModel(new Contract.RemoveModelRequest { ModelName = "gpt-5.4" },
            CreateContext());

        var provider = Assert.Single(response.Providers);
        Assert.Empty(provider.Models);
    }

    [Fact]
    public async Task SetModelEnabled_TogglesFlag_ReturnsRefreshedList()
    {
        var service = CreateService();

        var response = await service.SetModelEnabled(
            new Contract.SetModelEnabledRequest { ModelName = "gpt-5.4", Enabled = false }, CreateContext());

        var provider = Assert.Single(response.Providers);
        var model = Assert.Single(provider.Models);
        Assert.False(model.Enabled);
    }

    [Fact]
    public async Task ScanCapabilities_UpdatesAndReturnsRefreshedList()
    {
        using var temp = new TempDatabase();
        var service = CreateService(
            options: new ModelRoutingOptions
            {
                Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["lmstudio"] = new() { BaseUrl = "http://localhost:1234/v1" }
                }
            },
            dependencies: new ManagementFacadeDependencies
            {
                EndpointScanner = new ProviderEndpointScanner(httpClient: AlwaysOkModelList(),
                    environment: Mock.Of<IEnvironmentVariableProvider>()),
                CapabilityStore = temp.CreateToolCallCapabilityStore()
            });

        var response = await service.ScanCapabilities(
            new Contract.ScanCapabilitiesRequest { ProviderKey = "lmstudio" }, CreateContext());

        var provider = Assert.Single(response.Providers);
        Assert.NotNull(provider.EndpointCapabilities);
    }

    [Fact]
    public async Task RefreshFromEndpoint_DiscoversAndReconciles_ReturnsRefreshedList()
    {
        using var temp = new TempDatabase();
        var service = CreateService(
            options: new ModelRoutingOptions
            {
                Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new() { BaseUrl = "https://api.openai.com", AuthHeaderName = "Authorization" }
                }
            },
            dependencies: new ManagementFacadeDependencies
            {
                EndpointScanner = new ProviderEndpointScanner(httpClient: AlwaysOkModelList(),
                    environment: Mock.Of<IEnvironmentVariableProvider>()),
                CapabilityStore = temp.CreateToolCallCapabilityStore()
            },
            httpClient: AlwaysOkModelList());

        var response = await service.RefreshFromEndpoint(
            new Contract.RefreshFromEndpointRequest { ProviderKey = "openai" }, CreateContext());

        var provider = Assert.Single(response.Providers);
        Assert.Contains(provider.Models, m => m.ModelName == "gpt-5.4");
    }
}
