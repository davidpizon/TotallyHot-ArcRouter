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
/// and the MCP provider tools. These tests are the critical guarantee - a custom-header value must never
/// appear in anything the facade returns, and a blank write must preserve whatever secret is already
/// stored.
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
                AuthHeaderName = "Authorization",
                Headers = [new ProviderHeader { Name = "X-Literal", Value = "literal-secret" }]
            }
        },
        ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" }]
    };

    private static ManagementFacade CreateFacade(
        IProviderConfigStore? store = null,
        ProviderBudgetStore? budgetStore = null,
        PriceCatalogRepository? priceCatalogRepository = null) =>
        new(
            store ?? new InMemoryProviderConfigStore(SeedOptions()),
            Mock.Of<IEnvironmentVariableProvider>(),
            new HttpClient(),
            budgetStore,
            priceCatalogRepository: priceCatalogRepository);

    [Fact]
    public void ListProviders_NeverReturnsALockedHeaderValue()
    {
        var facade = CreateFacade();

        var response = facade.ListProviders();

        var header = Assert.Single(Assert.Single(response.Providers).Headers);
        Assert.Equal("X-Literal", header.Name);
        Assert.Equal(HeaderValueSource.Literal, header.Source);
        Assert.Null(header.ValueEnvVar);
        Assert.True(header.Locked);
        // The whole point of the lock: "literal-secret" is stored, is still sent upstream, and is not in
        // anything this facade hands back.
        Assert.Null(header.Value);
    }

    [Fact]
    public void ListProviders_ReturnsAnUnlockedHeaderValueInFull()
    {
        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic"] = new ProviderOptions
                {
                    BaseUrl = "https://api.anthropic.com",
                    Headers = [new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01", Locked = false }]
                }
            }
        });

        var header = Assert.Single(Assert.Single(CreateFacade(store).ListProviders().Providers).Headers);

        // Public configuration must come back readable, or the editor cannot show it.
        Assert.Equal(HeaderValueSource.Literal, header.Source);
        Assert.False(header.Locked);
        Assert.Equal("2023-06-01", header.Value);
    }

    [Fact]
    public void ListProviders_HeaderStoredBeforeTheLockedFlagExisted_IsReportedLocked()
    {
        // The migration guarantee: a header persisted without the flag has unknown provenance, so it must
        // stay hidden rather than become visible on upgrade.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ProviderHeader>(
            """{"Name":"X-Legacy","Value":"who-knows"}""")!;

        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderOptions { BaseUrl = "https://api.openai.com", Headers = [legacy] }
            }
        });

        var header = Assert.Single(Assert.Single(CreateFacade(store).ListProviders().Providers).Headers);

        Assert.True(legacy.Locked);
        Assert.True(header.Locked);
        Assert.Null(header.Value);
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
    public async Task UpsertProviderAsync_HeaderBothBlank_PreservesExistingLiteralHeaderValue()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        // The caller can't have received "literal-secret" from any prior read (write-only), so sending it
        // back blank must mean "keep what's there".
        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
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
            Headers: [new HeaderWriteRequest(Name: "X-Literal", Value: "new-secret", ValueEnvVar: null)]);

        await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        Assert.Equal("new-secret", store.Snapshot.Options.Providers["openai"].Headers.Single().Value);
    }

    [Fact]
    public async Task UpsertProviderAsync_LockedHeaderBlank_PreservesExistingLiteralHeaderValue()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        // A locked header's value was never returned, so the caller could not resend it: blank keeps it.
        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            Headers: [new HeaderWriteRequest(Name: "X-Literal", Value: null, ValueEnvVar: null, Locked: true)]);

        await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        var stored = store.Snapshot.Options.Providers["openai"].Headers.Single();
        Assert.Equal("literal-secret", stored.Value);
        Assert.True(stored.Locked);
    }

    [Fact]
    public async Task UpsertProviderAsync_ExplicitlyUnlockedHeaderBlank_ClearsTheStoredValue()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        // This is the editor's unlock reaching storage: the caller was shown the field in full and left it
        // empty, so blank means blank. Preserving here would leave a secret the operator believes is gone.
        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            Headers: [new HeaderWriteRequest(Name: "X-Literal", Value: null, ValueEnvVar: null, Locked: false)]);

        await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        var stored = store.Snapshot.Options.Providers["openai"].Headers.Single();
        Assert.Null(stored.Value);
        Assert.False(stored.Locked);
    }

    [Fact]
    public async Task UpsertProviderAsync_LockingAnExistingHeader_DoesNotRequireResendingTheValue()
    {
        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderOptions
                {
                    BaseUrl = "https://api.openai.com",
                    Headers = [new ProviderHeader { Name = "X-Literal", Value = "was-public", Locked = false }]
                }
            }
        });
        var facade = CreateFacade(store);

        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            Headers: [new HeaderWriteRequest(Name: "X-Literal", Value: null, ValueEnvVar: null, Locked: true)]);

        await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        var stored = store.Snapshot.Options.Providers["openai"].Headers.Single();
        Assert.Equal("was-public", stored.Value);
        Assert.True(stored.Locked);
    }

    [Fact]
    public async Task UpsertProviderAsync_LegacyHeaderWriteWithoutTheFlag_PreservesBlankAndStoresLocked()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        // Callers that predate the flag (MCP, hand-rolled REST) omit it; their headers must keep the old
        // write-only meaning rather than silently becoming readable.
        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            Headers:
            [
                new HeaderWriteRequest(Name: "X-Literal", Value: null, ValueEnvVar: null),
                new HeaderWriteRequest(Name: "X-New", Value: "fresh-secret", ValueEnvVar: null)
            ]);

        await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        var stored = store.Snapshot.Options.Providers["openai"].Headers;
        Assert.Equal("literal-secret", stored.Single(h => h.Name == "X-Literal").Value);
        Assert.True(stored.Single(h => h.Name == "X-New").Locked);
    }

    [Fact]
    public async Task UpsertProviderAsync_EnvVarHeader_AlwaysStoresUnlocked()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        // An env-var header holds a variable name, not a secret - there is nothing for a lock to withhold.
        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            Headers: [new HeaderWriteRequest(Name: "X-Literal", Value: null, ValueEnvVar: "SOME_VAR", Locked: true)]);

        await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        Assert.False(store.Snapshot.Options.Providers["openai"].Headers.Single().Locked);
    }

    [Fact]
    public async Task UpsertProviderAsync_HeaderSwitchToEnvVar_ClearsLiteralValue()
    {
        var store = new InMemoryProviderConfigStore(SeedOptions());
        var facade = CreateFacade(store);

        var request = new ProviderWriteRequest(
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: null,
            Headers: [new HeaderWriteRequest(Name: "X-Literal", Value: null, ValueEnvVar: "SOME_VAR")]);

        await facade.UpsertProviderAsync("openai", request, TestContext.Current.CancellationToken);

        var stored = store.Snapshot.Options.Providers["openai"].Headers.Single();
        Assert.Null(stored.Value);
        Assert.Equal("SOME_VAR", stored.ValueEnvVar);
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

    [Fact]
    public void ListProviders_NoPriceCatalogRepository_RateLimitIsNull()
    {
        var facade = CreateFacade(priceCatalogRepository: null);

        var provider = facade.ListProviders().Providers.Single();

        Assert.Null(provider.RateLimit);
    }

    [Fact]
    public void ListProviders_NoHeadersCapturedYet_RateLimitIsNull()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(priceCatalogRepository: temp.CreateRepository());

        var provider = facade.ListProviders().Providers.Single();

        Assert.Null(provider.RateLimit);
    }

    [Fact]
    public void ListProviders_HeadersCaptured_PopulatesRateLimitSnapshotAndObservedAt()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var observedAt = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            observedAt);
        var facade = CreateFacade(priceCatalogRepository: repository);

        var provider = facade.ListProviders().Providers.Single();

        Assert.NotNull(provider.RateLimit);
        Assert.Equal(observedAt, provider.RateLimit!.ObservedAtUtc);
        Assert.Equal(1000, provider.RateLimit.Snapshot.StandardDimensions["tokens"].Remaining);
    }

    [Fact]
    public void ListProviders_NoUsageRecorded_UsageLastRecordedAtUtcIsNull()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(budgetStore: temp.CreateBudgetStore());

        var provider = facade.ListProviders().Providers.Single();

        Assert.Null(provider.UsageLastRecordedAtUtc);
    }

    [Fact]
    public async Task ListProviders_UsageRecorded_UsageLastRecordedAtUtcReflectsIt()
    {
        using var temp = new TempDatabase();
        var budgetStore = temp.CreateBudgetStore();
        var usageAt = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        await budgetStore.RecordUsageAsync("openai", 1m, 10, 5, null, null, usageAt, TestContext.Current.CancellationToken);
        var facade = CreateFacade(budgetStore: budgetStore);

        var provider = facade.ListProviders().Providers.Single();

        Assert.Equal(usageAt, provider.UsageLastRecordedAtUtc);
    }
}

