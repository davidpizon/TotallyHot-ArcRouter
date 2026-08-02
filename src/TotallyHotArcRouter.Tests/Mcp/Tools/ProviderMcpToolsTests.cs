using TotallyHot.ArcRouter.Mcp.Tools;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Tests.Proxy;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Mcp.Tools;

/// <summary>
/// Covers <see cref="ProviderMcpTools"/>: each method delegates to the shared
/// <see cref="ManagementFacade"/> and returns masked output - the same guarantee
/// <c>ManagementFacadeTests</c> covers in depth, verified here at the tool boundary.
/// </summary>
public sealed class ProviderMcpToolsTests
{
    private static ModelRoutingOptions SeedOptions() => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new ProviderOptions { BaseUrl = "https://api.openai.com", ApiKey = "sk-secret", AuthHeaderName = "Authorization" }
        },
        ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }]
    };

    private static ProviderMcpTools CreateTools(out InMemoryProviderConfigStore store)
    {
        store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = new ManagementFacade(store, Mock.Of<IEnvironmentVariableProvider>(), new HttpClient());
        return new ProviderMcpTools(facade);
    }

    [Fact]
    public void ListProviders_NeverReturnsLiteralApiKey()
    {
        var tools = CreateTools(out _);

        var response = tools.ListProviders();

        var provider = Assert.Single(response.Providers);
        Assert.True(provider.HasApiKey);
    }

    [Fact]
    public async Task UpsertProviderAsync_ValidEdit_UpdatesStoreAndReturnsProvidersResponse()
    {
        var tools = CreateTools(out var store);

        var result = await tools.UpsertProviderAsync(
            "openai",
            new ProviderWriteRequest(BaseUrl: "https://api.openai.com/v2", AuthHeaderName: null, AuthHeaderScheme: null, ApiKey: null, ApiKeyEnvVar: null),
            TestContext.Current.CancellationToken);

        Assert.IsType<ProvidersResponse>(result);
        Assert.Equal("https://api.openai.com/v2", store.Snapshot.Options.Providers["openai"].BaseUrl);
    }

    [Fact]
    public async Task RemoveProviderAsync_Unknown_ReturnsErrorShapedObject()
    {
        var tools = CreateTools(out _);

        var result = await tools.RemoveProviderAsync("nope", TestContext.Current.CancellationToken);

        var errorProperty = result.GetType().GetProperty("error");
        Assert.NotNull(errorProperty);
        Assert.NotNull(errorProperty!.GetValue(result));
    }

    [Fact]
    public void SetProviderBudget_NoBudgetStoreConfigured_ReturnsUnavailableErrorShapedObject()
    {
        var tools = CreateTools(out _);

        var result = tools.SetProviderBudget("openai", new ProviderBudgetWriteRequest(10m, null));

        var typeProperty = result.GetType().GetProperty("type");
        Assert.NotNull(typeProperty);
        Assert.Equal(nameof(ManagementErrorType.Unavailable), typeProperty!.GetValue(result));
    }
}

