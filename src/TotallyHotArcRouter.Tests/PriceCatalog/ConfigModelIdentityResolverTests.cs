using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Tests.Proxy;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="ConfigModelIdentityResolver"/>'s §5.7 resolution ladder: mapping each aggregator's own
/// model/provider naming onto the configured <c>ModelRouting:ModelList</c> identity, in order of rung.
/// </summary>
public class ConfigModelIdentityResolverTests
{
    private const string Source = "LiteLLM";

    private static InMemoryProviderConfigStore StoreWith(params ModelRouteEntry[] models)
    {
        var providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in models.Select(m => m.Provider).Distinct(StringComparer.OrdinalIgnoreCase))
            providers[provider] = new ProviderOptions { BaseUrl = "https://example.com" };

        return new InMemoryProviderConfigStore(new ModelRoutingOptions
        { Providers = providers, ModelList = [.. models] });
    }

    private static ModelRouteEntry Entry(string modelName, string provider, string providerModelId)
    {
        return new ModelRouteEntry { ModelName = modelName, Provider = provider, ProviderModelId = providerModelId };
    }

    [Fact]
    public void Resolve_LiteLlmBareName_ResolvesToConfiguredIdentity_AtExactRung()
    {
        var resolver =
            new ConfigModelIdentityResolver(StoreWith(Entry(modelName: "gpt-5.4", provider: "openai",
                providerModelId: "gpt-4o")));

        var resolution =
            resolver.Resolve(sourceName: Source, aggregatorModelId: "gpt-4o", aggregatorProvider: "openai");

        Assert.Equal(
            expected: new IdentityResolution(
                Identity: new ResolvedModelIdentity(ModelName: "gpt-5.4", Provider: "openai"),
                Rung: ResolutionRung.Exact), actual: resolution);
        Assert.False(resolution!.Value.IsApproximate);
    }

    [Fact]
    public void Resolve_OpenRouterPrefixedId_StripsProviderPrefixAndResolves()
    {
        // OpenRouter names the same model "openai/gpt-4o"; its provider prefix is stripped so it maps onto the
        // same configured entry LiteLLM's "gpt-4o" does - which is exactly what makes the two collide into one
        // (model, provider) cell at ingest.
        var resolver =
            new ConfigModelIdentityResolver(StoreWith(Entry(modelName: "gpt-5.4", provider: "openai",
                providerModelId: "gpt-4o")));

        var resolution = resolver.Resolve(sourceName: Source, aggregatorModelId: "openai/gpt-4o",
            aggregatorProvider: "openai");

        Assert.Equal(expected: ResolutionRung.Exact, actual: resolution!.Value.Rung);
        Assert.Equal(expected: new ResolvedModelIdentity(ModelName: "gpt-5.4", Provider: "openai"),
            actual: resolution.Value.Identity);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive_OnBothProviderAndModelId()
    {
        var resolver =
            new ConfigModelIdentityResolver(StoreWith(Entry(modelName: "gpt-5.4", provider: "openai",
                providerModelId: "gpt-4o")));

        var resolution =
            resolver.Resolve(sourceName: Source, aggregatorModelId: "GPT-4O", aggregatorProvider: "OpenAI");

        Assert.Equal(expected: new ResolvedModelIdentity(ModelName: "gpt-5.4", Provider: "openai"),
            actual: resolution!.Value.Identity);
    }

    [Fact]
    public void Resolve_ProviderMismatch_FallsThroughToProviderAliasRung()
    {
        // Same model id, different provider: not an exact match, but the ladder's last rung matches on model
        // id alone across providers - and flags the result approximate.
        var resolver =
            new ConfigModelIdentityResolver(StoreWith(Entry(modelName: "gpt-5.4", provider: "openai",
                providerModelId: "gpt-4o")));

        var resolution = resolver.Resolve(sourceName: Source, aggregatorModelId: "gpt-4o",
            aggregatorProvider: "anthropic");

        Assert.Equal(expected: ResolutionRung.ProviderAlias, actual: resolution!.Value.Rung);
        Assert.True(resolution.Value.IsApproximate);
        Assert.Equal(expected: new ResolvedModelIdentity(ModelName: "gpt-5.4", Provider: "openai"),
            actual: resolution.Value.Identity);
    }

    [Fact]
    public void Resolve_UnknownModelId_ReturnsNull()
    {
        var resolver =
            new ConfigModelIdentityResolver(StoreWith(Entry(modelName: "gpt-5.4", provider: "openai",
                providerModelId: "gpt-4o")));

        Assert.Null(resolver.Resolve(sourceName: Source, aggregatorModelId: "some-model-nobody-configured",
            aggregatorProvider: "openai"));
    }

    [Fact]
    public void Resolve_DoesNotStripSlashThatIsNotTheProviderPrefix()
    {
        // The prefix stripped is the source's own provider only, not any leading slash-segment: a model id
        // that legitimately contains a slash keeps it, so it can still match a ProviderModelId that has one.
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry(modelName: "llama", provider: "together",
            providerModelId: "meta-llama/llama-3")));

        var resolution = resolver.Resolve(sourceName: Source, aggregatorModelId: "meta-llama/llama-3",
            aggregatorProvider: "together");

        Assert.Equal(expected: new ResolvedModelIdentity(ModelName: "llama", Provider: "together"),
            actual: resolution!.Value.Identity);
        Assert.Equal(expected: ResolutionRung.Exact, actual: resolution.Value.Rung);
    }

    [Fact]
    public void Resolve_DatedSnapshotSuffix_MatchesAtSnapshotSuffixStrippedRung()
    {
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry(modelName: "claude-sonnet-5",
            provider: "anthropic", providerModelId: "claude-sonnet-4-5")));

        var resolution = resolver.Resolve(sourceName: Source, aggregatorModelId: "claude-sonnet-4-5-20250929",
            aggregatorProvider: "anthropic");

        Assert.Equal(expected: ResolutionRung.SnapshotSuffixStripped, actual: resolution!.Value.Rung);
        Assert.True(resolution.Value.IsApproximate);
        Assert.Equal(expected: new ResolvedModelIdentity(ModelName: "claude-sonnet-5", Provider: "anthropic"),
            actual: resolution.Value.Identity);
    }

    [Fact]
    public void Resolve_VersionTierSuffix_MatchesAtVersionNormalizedRung()
    {
        var resolver =
            new ConfigModelIdentityResolver(StoreWith(Entry(modelName: "gpt-5.4", provider: "openai",
                providerModelId: "gpt-4o")));

        var resolution = resolver.Resolve(sourceName: Source, aggregatorModelId: "gpt-4o-latest",
            aggregatorProvider: "openai");

        Assert.Equal(expected: ResolutionRung.VersionNormalized, actual: resolution!.Value.Rung);
        Assert.True(resolution.Value.IsApproximate);
    }

    [Fact]
    public void Resolve_OperatorOverride_TakesPrecedenceOverExactMatch()
    {
        using var db = new TempDatabase();
        var store = StoreWith(Entry(modelName: "gpt-5.4", provider: "openai", providerModelId: "gpt-4o"),
            Entry(modelName: "custom-model", provider: "openai", providerModelId: "big-pickle"));
        var overrideStore = db.CreateOverrideStore();
        overrideStore.Upsert(sourceName: Source, aggregatorModelKey: "gpt-4o", modelName: "custom-model");
        var resolver = new ConfigModelIdentityResolver(configStore: store, overrideStore: overrideStore);

        var resolution =
            resolver.Resolve(sourceName: Source, aggregatorModelId: "gpt-4o", aggregatorProvider: "openai");

        Assert.Equal(expected: ResolutionRung.OperatorOverride, actual: resolution!.Value.Rung);
        Assert.False(resolution.Value.IsApproximate);
        Assert.Equal(expected: new ResolvedModelIdentity(ModelName: "custom-model", Provider: "openai"),
            actual: resolution.Value.Identity);
    }

    [Fact]
    public void Resolve_OperatorOverride_IgnoredForOtherSources()
    {
        using var db = new TempDatabase();
        var store = StoreWith(Entry(modelName: "gpt-5.4", provider: "openai", providerModelId: "gpt-4o"),
            Entry(modelName: "custom-model", provider: "openai", providerModelId: "big-pickle"));
        var overrideStore = db.CreateOverrideStore();
        overrideStore.Upsert(sourceName: "OpenRouter", aggregatorModelKey: "gpt-4o", modelName: "custom-model");
        var resolver = new ConfigModelIdentityResolver(configStore: store, overrideStore: overrideStore);

        var resolution =
            resolver.Resolve(sourceName: Source, aggregatorModelId: "gpt-4o", aggregatorProvider: "openai");

        Assert.Equal(expected: ResolutionRung.Exact, actual: resolution!.Value.Rung);
    }

    [Fact]
    public async Task Resolve_PicksUpConfigEdits_WithoutRebuildingResolver()
    {
        // Live reload: the resolver reads the current snapshot, so a model added at runtime resolves on the
        // next call without reconstructing anything - the version bump invalidates the cached index.
        var store = StoreWith(Entry(modelName: "gpt-5.4", provider: "openai", providerModelId: "gpt-4o"));
        var resolver = new ConfigModelIdentityResolver(store);

        Assert.Null(
            resolver.Resolve(sourceName: Source, aggregatorModelId: "claude-4", aggregatorProvider: "anthropic"));

        await store.UpsertProviderAsync(key: "anthropic",
            provider: new ProviderOptions { BaseUrl = "https://example.com" },
            cancellationToken: TestContext.Current.CancellationToken);
        await store.UpsertModelAsync(
            entry: Entry(modelName: "claude-opus-4.6", provider: "anthropic", providerModelId: "claude-4"),
            cancellationToken: TestContext.Current.CancellationToken);

        var resolution = resolver.Resolve(sourceName: Source, aggregatorModelId: "claude-4",
            aggregatorProvider: "anthropic");
        Assert.Equal(expected: new ResolvedModelIdentity(ModelName: "claude-opus-4.6", Provider: "anthropic"),
            actual: resolution!.Value.Identity);
    }
}