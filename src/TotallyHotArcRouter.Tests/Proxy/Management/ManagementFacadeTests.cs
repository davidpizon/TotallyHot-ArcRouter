using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Tests.PriceCatalog;
using TotallyHot.ArcRouter.Tests.Proxy;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Covers <see cref="ManagementFacade"/>: the shared security boundary behind both REST <c>/admin/*</c>
/// and the MCP provider tools. These tests are the critical guarantee - a literal API key or custom-header
/// value must never appear in anything the facade returns, and a blank write must preserve whatever secret
/// is already stored, on both the API key and header paths alike.
/// </summary>
public sealed class ManagementFacadeTests
{
    private static ModelRoutingOptions SeedOptions() => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new ProviderOptions
            {
                BaseUrl = "https://api.openai.com",
                ApiKey = "sk-secret",
                AuthHeaderName = "Authorization",
                Headers = [new ProviderHeader { Name = "X-Literal", Value = "literal-secret" }]
            }
        },
        ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }]
    };

    private static ManagementFacade CreateFacade(IProviderConfigStore? store = null, ProviderBudgetStore? budgetStore = null) =>
        new(store ?? new InMemoryProviderConfigStore(SeedOptions()), Mock.Of<IEnvironmentVariableProvider>(), new HttpClient(), budgetStore);

    [Fact]
    public void ListProviders_NeverReturnsLiteralApiKey()
    {
        var facade = CreateFacade();

        var response = facade.ListProviders();

        var provider = Assert.Single(response.Providers);
        Assert.True(provider.HasApiKey);
        Assert.Null(provider.ApiKeyEnvVar);
        // No property on ProviderView carries the literal key - HasApiKey/ApiKeyEnvVar are the only
        // credential-shaped fields, and neither is the secret itself.
    }

    [Fact]
    public void ListProviders_NeverReturnsLiteralHeaderValue()
    {
        var facade = CreateFacade();

        var response = facade.ListProviders();

        var header = Assert.Single(Assert.Single(response.Providers).Headers);
        Assert.Equal("X-Literal", header.Name);
        Assert.Equal(HeaderValueSource.Literal, header.Source);
        Assert.Null(header.ValueEnvVar);
        // HeaderView has no Value property at all - the literal "literal-secret" cannot be projected
        // through it even by accident.
    }

    [Fact]
    public void ListProviders_EnvVarHeader_ReportsEnvVarSourceAndName()
    {
        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderOptions
                {
                    BaseUrl = "https://api.openai.com",
                    Headers = [new ProviderHeader { Name = "X-Env", ValueEnvVar = "SOME_VAR" }]
                }
            },
            ModelList = []
        });
        var facade = CreateFacade(store);

        var header = Assert.Single(Assert.Single(facade.ListProviders().Providers).Headers);

        Assert.Equal(HeaderValueSource.EnvVar, header.Source);
        Assert.Equal("SOME_VAR", header.ValueEnvVar);
    }

    [Fact]
    public void ListProviders_HeaderWithBothLiteralAndEnvVarSet_ClassifiesAsLiteralAndOmitsEnvVarName()
    {
        // Legacy/bad data: a header row with both fields set. ClassifyHeaderSource picks literal first, so
        // ValueEnvVar must not also be surfaced - HeaderView's contract is that ValueEnvVar is only
        // meaningful when Source is "envVar".
        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderOptions
                {
                    BaseUrl = "https://api.openai.com",
                    Headers = [new ProviderHeader { Name = "X-Both", Value = "literal-secret", ValueEnvVar = "SOME_VAR" }]
                }
            },
            ModelList = []
        });
        var facade = CreateFacade(store);

        var header = Assert.Single(Assert.Single(facade.ListProviders().Providers).Headers);

        Assert.Equal(HeaderValueSource.Literal, header.Source);
        Assert.Null(header.ValueEnvVar);
    }

    [Fact]
    public async Task UpsertProviderAsync_LiteralModeBlankKey_PreservesExistingKeyButNeverReturnsIt()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        var result = await facade.UpsertProviderAsync(
            "openai",
            new ProviderWriteRequest(BaseUrl: "https://api.openai.com/v2", AuthHeaderName: null, AuthHeaderScheme: null, ApiKey: null, ApiKeyEnvVar: null, CredentialMode: "literal"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // The store still has the original secret (edit-without-resending-the-key worked)...
        Assert.Equal("sk-secret", store.Snapshot.Options.Providers["openai"].ApiKey);
        // ...but the facade's own response never carries it, anywhere.
        Assert.True(result.Value!.Providers.Single().HasApiKey);
    }

    [Fact]
    public async Task UpsertProviderAsync_HeaderBothBlank_PreservesExistingLiteralHeaderValue()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        // The caller can't have received "literal-secret" from any prior read (write-only), so sending it
        // back blank must mean "keep what's there", exactly like the API key's literal-mode blank rule.
        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null,
            CredentialMode: "literal",
            Headers: [new HeaderWriteRequest(Name: "X-Literal", Value: null, ValueEnvVar: null)]);

        var result = await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var stored = store.Snapshot.Options.Providers["openai"].Headers.Single();
        Assert.Equal("literal-secret", stored.Value);
        Assert.Null(stored.ValueEnvVar);
    }

    [Fact]
    public async Task UpsertProviderAsync_HeaderBothBlank_DifferentCasingStillPreservesExistingValue()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        // HTTP header names are case-insensitive: a caller resending "x-literal" (stored as "X-Literal")
        // with a blank value must still preserve the stored secret, not treat it as a different header.
        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null,
            Headers: [new HeaderWriteRequest(Name: "x-literal", Value: null, ValueEnvVar: null)]);

        var result = await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var stored = store.Snapshot.Options.Providers["openai"].Headers.Single();
        Assert.Equal("literal-secret", stored.Value);
    }

    [Fact]
    public async Task UpsertProviderAsync_HeaderWithNewLiteralValue_ReplacesStoredValue()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null,
            Headers: [new HeaderWriteRequest(Name: "X-Literal", Value: "new-secret", ValueEnvVar: null)]);

        await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        Assert.Equal("new-secret", store.Snapshot.Options.Providers["openai"].Headers.Single().Value);
    }

    [Fact]
    public async Task UpsertProviderAsync_HeaderSwitchToEnvVar_ClearsLiteralValue()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null,
            Headers: [new HeaderWriteRequest(Name: "X-Literal", Value: null, ValueEnvVar: "SOME_VAR")]);

        await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        var stored = store.Snapshot.Options.Providers["openai"].Headers.Single();
        Assert.Null(stored.Value);
        Assert.Equal("SOME_VAR", stored.ValueEnvVar);
    }

    [Fact]
    public async Task UpsertProviderAsync_CredentialModeNone_ClearsApiKeyAndEnvVar()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        await facade.UpsertProviderAsync(
            "openai",
            new ProviderWriteRequest(BaseUrl: "https://api.openai.com", AuthHeaderName: null, AuthHeaderScheme: null, ApiKey: null, ApiKeyEnvVar: null, CredentialMode: "none"),
            TestContext.Current.CancellationToken);

        var stored = store.Snapshot.Options.Providers["openai"];
        Assert.Null(stored.ApiKey);
        Assert.Null(stored.ApiKeyEnvVar);
    }

    [Fact]
    public async Task RemoveProviderAsync_Unknown_ReturnsNotFound()
    {
        var facade = CreateFacade();

        var result = await facade.RemoveProviderAsync("nope", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.NotFound, result.ErrorType);
    }

    [Fact]
    public async Task RemoveProviderAsync_StillReferencedByModel_CascadesAndSucceeds()
    {
        var facade = CreateFacade();

        // The seed's only model routes to "openai". Removal takes the model with it rather than being
        // rejected, so the response reflects a config with neither the provider nor its model left.
        var result = await facade.RemoveProviderAsync("openai", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(result.Value!.Providers);
    }

    [Fact]
    public async Task RemoveModelAsync_Unknown_ReturnsNotFound()
    {
        var facade = CreateFacade();

        var result = await facade.RemoveModelAsync("no-such-model", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.NotFound, result.ErrorType);
    }

    [Fact]
    public void SetBudget_NoBudgetStore_ReturnsUnavailable()
    {
        var facade = CreateFacade(budgetStore: null);

        var result = facade.SetBudget("openai", new ProviderBudgetWriteRequest(10m, null));

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void SetBudget_NegativeCap_ReturnsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(budgetStore: temp.CreateBudgetStore());

        var result = facade.SetBudget("openai", new ProviderBudgetWriteRequest(-1m, null));

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public void SetBudget_UnknownProvider_ReturnsNotFound()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(budgetStore: temp.CreateBudgetStore());

        var result = facade.SetBudget("nope", new ProviderBudgetWriteRequest(10m, null));

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.NotFound, result.ErrorType);
    }

    [Fact]
    public void SetBudget_ValidCaps_PersistsAndSurfacesOnListProviders()
    {
        using var temp = new TempDatabase();
        var budgetStore = temp.CreateBudgetStore();
        var facade = CreateFacade(budgetStore: budgetStore);

        var result = facade.SetBudget("openai", new ProviderBudgetWriteRequest(500m, 1_000_000L));

        Assert.True(result.Success);
        var provider = result.Value!.Providers.Single();
        Assert.Equal(500m, provider.DollarCap);
        Assert.Equal(1_000_000L, provider.TokenCap);
    }
}

