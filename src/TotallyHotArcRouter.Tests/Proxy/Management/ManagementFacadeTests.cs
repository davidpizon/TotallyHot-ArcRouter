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
        PriceRepository? priceRepository = null,
        RateLimitRepository? rateLimitRepository = null,
        ModelAliasOverrideStore? overrideStore = null,
        TimeSpan? rateLimitStalenessThreshold = null) =>
        new(
            store ?? new InMemoryProviderConfigStore(SeedOptions()),
            Mock.Of<IEnvironmentVariableProvider>(),
            new HttpClient(),
            new ManagementFacadeDependencies
            {
                BudgetStore = budgetStore,
                PriceRepository = priceRepository,
                RateLimitRepository = rateLimitRepository,
                OverrideStore = overrideStore,
                RateLimitStalenessThreshold = rateLimitStalenessThreshold,
            });

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
    public void GetPriceResolutionDiagnosis_NoRepository_ReturnsUnavailable()
    {
        var facade = CreateFacade(priceRepository: null);

        var result = facade.GetPriceResolutionDiagnosis();

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void GetPriceResolutionDiagnosis_UnresolvedModel_ReportsUnresolved()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(priceRepository: temp.CreateRepository());

        var result = facade.GetPriceResolutionDiagnosis();

        Assert.True(result.Success);
        var row = Assert.Single(result.Value!);
        Assert.Equal("gpt-5.4", row.ModelName);
        Assert.False(row.Resolved);
        Assert.False(row.IsApproximate);
    }

    [Fact]
    public void GetPriceResolutionDiagnosis_ExactMatch_ReportsResolvedNotApproximate()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var price = new TotallyHot.ArcRouter.PriceCatalog.Sources.NormalizedPrice(
            ModelIdentifier: "gpt-5.4", Provider: "openai", StandardInputPrice: 2m, StandardOutputPrice: 6m,
            CachedInputPrice: null, BatchInputPrice: null, BatchOutputPrice: null);
        repository.UpsertPrices("litellm", 0, [price], DateTimeOffset.UtcNow);
        var facade = CreateFacade(priceRepository: repository);

        var row = Assert.Single(facade.GetPriceResolutionDiagnosis().Value!);

        Assert.True(row.Resolved);
        Assert.False(row.IsApproximate);
    }

    [Fact]
    public void ListPriceOverrides_NoOverrideStore_ReturnsUnavailable()
    {
        var facade = CreateFacade(overrideStore: null);

        var result = facade.ListPriceOverrides();

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void SetPriceOverride_UnconfiguredModel_ReturnsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(overrideStore: temp.CreateOverrideStore());

        var result = facade.SetPriceOverride(new PriceOverrideWriteRequest("LiteLLM", "big-pickle", "not-configured"));

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public void SetPriceOverride_MissingField_ReturnsInvalidRequest()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(overrideStore: temp.CreateOverrideStore());

        var result = facade.SetPriceOverride(new PriceOverrideWriteRequest("", "big-pickle", "gpt-5.4"));

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.InvalidRequest, result.ErrorType);
    }

    [Fact]
    public void SetPriceOverride_ConfiguredModel_PersistsAndListsIt()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(overrideStore: temp.CreateOverrideStore());

        var result = facade.SetPriceOverride(new PriceOverrideWriteRequest("LiteLLM", "big-pickle", "gpt-5.4"));

        Assert.True(result.Success);
        var o = Assert.Single(result.Value!);
        Assert.Equal(new ModelAliasOverride("LiteLLM", "big-pickle", "gpt-5.4"), o);
        Assert.Equal(o, Assert.Single(facade.ListPriceOverrides().Value!));
    }

    [Fact]
    public void RemovePriceOverride_NoMatch_ReturnsNotFound()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(overrideStore: temp.CreateOverrideStore());

        var result = facade.RemovePriceOverride("LiteLLM", "big-pickle");

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.NotFound, result.ErrorType);
    }

    [Fact]
    public void RemovePriceOverride_Existing_RemovesIt()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(overrideStore: temp.CreateOverrideStore());
        facade.SetPriceOverride(new PriceOverrideWriteRequest("LiteLLM", "big-pickle", "gpt-5.4"));

        var result = facade.RemovePriceOverride("LiteLLM", "big-pickle");

        Assert.True(result.Success);
        Assert.Empty(result.Value!);
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
        var facade = CreateFacade(rateLimitRepository: null);

        var provider = facade.ListProviders().Providers.Single();

        Assert.Null(provider.RateLimit);
    }

    [Fact]
    public void ListProviders_NoHeadersCapturedYet_RateLimitIsNull()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(rateLimitRepository: temp.CreateRateLimitRepository());

        var provider = facade.ListProviders().Providers.Single();

        Assert.Null(provider.RateLimit);
    }

    [Fact]
    public void ListProviders_HeadersCaptured_PopulatesRateLimitSnapshotAndObservedAt()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var observedAt = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            observedAt);
        var facade = CreateFacade(rateLimitRepository: repository);

        var provider = facade.ListProviders().Providers.Single();

        Assert.NotNull(provider.RateLimit);
        Assert.Equal(observedAt, provider.RateLimit!.ObservedAtUtc);
        Assert.Equal(1000, provider.RateLimit.Snapshot.StandardDimensions["tokens"].Remaining);
    }

    [Fact]
    public void ListProviders_RecentCapture_IsNotStale()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var facade = CreateFacade(rateLimitRepository: repository);

        var provider = facade.ListProviders().Providers.Single();

        Assert.False(provider.RateLimit!.IsStale);
    }

    [Fact]
    public void ListProviders_CaptureOlderThanStalenessThreshold_IsStale()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            DateTimeOffset.UtcNow.AddMinutes(-30));
        var facade = CreateFacade(rateLimitRepository: repository, rateLimitStalenessThreshold: TimeSpan.FromMinutes(15));

        var provider = facade.ListProviders().Providers.Single();

        Assert.True(provider.RateLimit!.IsStale);
    }

    [Fact]
    public void ListProviders_NoNewCaptureSinceLastLoad_LastGoodSnapshotStandsUnchanged()
    {
        // Pins the "last-good" contract (§5.9): a header-free response never clears/replaces a prior
        // snapshot - RateLimitHeaderCapture.CaptureAsync already no-ops on an empty header list (see
        // RateLimitRepositoryTests.UpsertRateLimitHeaders_EmptyList_IsNoOp), so simulating "no headers
        // this time" here is simply not calling UpsertRateLimitHeaders again.
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            observedAt);
        var facade = CreateFacade(rateLimitRepository: repository);

        var first = facade.ListProviders().Providers.Single().RateLimit;
        var second = facade.ListProviders().Providers.Single().RateLimit;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(observedAt, first!.ObservedAtUtc);
        Assert.Equal(observedAt, second!.ObservedAtUtc);
        Assert.Equal(1000, second.Snapshot.StandardDimensions["tokens"].Remaining);
    }

    [Fact]
    public void ListProviders_TwoHistoryObservations_PopulatesExhaustionProjection()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-20);
        var later = DateTimeOffset.UtcNow.AddMinutes(-10);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "10000")],
            earlier);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "8000")],
            later);
        var facade = CreateFacade(rateLimitRepository: repository);

        var rateLimit = facade.ListProviders().Providers.Single().RateLimit!;

        var projection = rateLimit.Projections["tokens"];
        Assert.True(projection.TimeToExhaustion > TimeSpan.Zero);
        Assert.True(projection.BurnRatePerMinute > 0);
    }

    [Fact]
    public void ListProviders_OnlyOneObservation_ProjectionsIsEmpty()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var facade = CreateFacade(rateLimitRepository: repository);

        var rateLimit = facade.ListProviders().Providers.Single().RateLimit!;

        Assert.Empty(rateLimit.Projections);
    }

    [Fact]
    public void GetRateLimitHistory_UnknownProvider_ReturnsNotFound()
    {
        using var temp = new TempDatabase();
        var facade = CreateFacade(rateLimitRepository: temp.CreateRateLimitRepository());

        var result = facade.GetRateLimitHistory("does-not-exist", hours: 6);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.NotFound, result.ErrorType);
    }

    [Fact]
    public void GetRateLimitHistory_NoPriceCatalogRepository_ReturnsUnavailable()
    {
        var facade = CreateFacade(rateLimitRepository: null);

        var result = facade.GetRateLimitHistory("openai", hours: 6);

        Assert.False(result.Success);
        Assert.Equal(ManagementErrorType.Unavailable, result.ErrorType);
    }

    [Fact]
    public void GetRateLimitHistory_ReturnsChronologicalPointsPerDimension()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);
        var second = DateTimeOffset.UtcNow.AddMinutes(-3);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            first);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "900")],
            second);
        var facade = CreateFacade(rateLimitRepository: repository);

        var result = facade.GetRateLimitHistory("openai", hours: 1);

        Assert.True(result.Success);
        var points = result.Value!.Dimensions["tokens"];
        Assert.Equal(3, points.Count);
        Assert.Equal(1000, points[0].Remaining);
        Assert.Null(points[1].Remaining);
        Assert.Null(points[1].Limit);
        Assert.Equal(points[0].BucketUtc.AddMinutes(1), points[1].BucketUtc);
        Assert.Equal(900, points[2].Remaining);
    }

    [Fact]
    public void GetRateLimitHistory_NoGapBetweenBuckets_DoesNotInsertNullPoint()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var first = DateTimeOffset.UtcNow.AddMinutes(-2);
        var second = DateTimeOffset.UtcNow.AddMinutes(-1);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            first);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "900")],
            second);
        var facade = CreateFacade(rateLimitRepository: repository);

        var result = facade.GetRateLimitHistory("openai", hours: 1);

        Assert.True(result.Success);
        var points = result.Value!.Dimensions["tokens"];
        Assert.Equal(2, points.Count);
        Assert.Equal(1000, points[0].Remaining);
        Assert.Equal(900, points[1].Remaining);
    }

    [Fact]
    public void GetRateLimitHistory_DimensionMissingFromOneBucketButPresentInAnother_InsertsNullPointForThatBucket()
    {
        // First bucket captures both dimensions; second bucket (one minute later, no time gap) only
        // captured "requests" - the "tokens" header was absent/unparsable that minute. "tokens" must still
        // get an explicit null point at the second bucket's timestamp rather than simply skipping the
        // x-value, so the stepped chart (connectNulls: false) renders a gap instead of holding steady.
        using var temp = new TempDatabase();
        var repository = temp.CreateRateLimitRepository();
        var first = DateTimeOffset.UtcNow.AddMinutes(-2);
        var second = DateTimeOffset.UtcNow.AddMinutes(-1);
        repository.UpsertRateLimitHeaders(
            "openai",
            [
                new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000"),
                new RateLimitHeaderRow("anthropic-ratelimit-requests-remaining", "50"),
            ],
            first);
        repository.UpsertRateLimitHeaders(
            "openai",
            [new RateLimitHeaderRow("anthropic-ratelimit-requests-remaining", "49")],
            second);
        var facade = CreateFacade(rateLimitRepository: repository);

        var result = facade.GetRateLimitHistory("openai", hours: 1);

        Assert.True(result.Success);
        var tokenPoints = result.Value!.Dimensions["tokens"];
        Assert.Equal(2, tokenPoints.Count);
        Assert.Equal(1000, tokenPoints[0].Remaining);
        Assert.Null(tokenPoints[1].Remaining);
        Assert.Null(tokenPoints[1].Limit);
        Assert.Equal(tokenPoints[0].BucketUtc.AddMinutes(1), tokenPoints[1].BucketUtc);

        var requestPoints = result.Value.Dimensions["requests"];
        Assert.Equal(2, requestPoints.Count);
        Assert.Equal(50, requestPoints[0].Remaining);
        Assert.Equal(49, requestPoints[1].Remaining);
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

