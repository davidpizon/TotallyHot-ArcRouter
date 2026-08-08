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
        {
            providers[provider] = new ProviderOptions { BaseUrl = "https://example.com" };
        }

        return new InMemoryProviderConfigStore(new ModelRoutingOptions { Providers = providers, ModelList = [.. models] });
    }

    private static ModelRouteEntry Entry(string modelName, string provider, string providerModelId) =>
        new() { ModelName = modelName, Provider = provider, ProviderModelId = providerModelId };

    [Fact]
    public void Resolve_LiteLlmBareName_ResolvesToConfiguredIdentity_AtExactRung()
    {
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        var resolution = resolver.Resolve(Source, "gpt-4o", "openai");

        Assert.Equal(new IdentityResolution(new ResolvedModelIdentity("gpt-5.4", "openai"), ResolutionRung.Exact), resolution);
        Assert.False(resolution!.Value.IsApproximate);
    }

    [Fact]
    public void Resolve_OpenRouterPrefixedId_StripsProviderPrefixAndResolves()
    {
        // OpenRouter names the same model "openai/gpt-4o"; its provider prefix is stripped so it maps onto the
        // same configured entry LiteLLM's "gpt-4o" does - which is exactly what makes the two collide into one
        // (model, provider) cell at ingest.
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        var resolution = resolver.Resolve(Source, "openai/gpt-4o", "openai");

        Assert.Equal(ResolutionRung.Exact, resolution!.Value.Rung);
        Assert.Equal(new ResolvedModelIdentity("gpt-5.4", "openai"), resolution.Value.Identity);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive_OnBothProviderAndModelId()
    {
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        var resolution = resolver.Resolve(Source, "GPT-4O", "OpenAI");

        Assert.Equal(new ResolvedModelIdentity("gpt-5.4", "openai"), resolution!.Value.Identity);
    }

    [Fact]
    public void Resolve_ProviderMismatch_FallsThroughToProviderAliasRung()
    {
        // Same model id, different provider: not an exact match, but the ladder's last rung matches on model
        // id alone across providers - and flags the result approximate.
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        var resolution = resolver.Resolve(Source, "gpt-4o", "anthropic");

        Assert.Equal(ResolutionRung.ProviderAlias, resolution!.Value.Rung);
        Assert.True(resolution.Value.IsApproximate);
        Assert.Equal(new ResolvedModelIdentity("gpt-5.4", "openai"), resolution.Value.Identity);
    }

    [Fact]
    public void Resolve_UnknownModelId_ReturnsNull()
    {
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        Assert.Null(resolver.Resolve(Source, "some-model-nobody-configured", "openai"));
    }

    [Fact]
    public void Resolve_DoesNotStripSlashThatIsNotTheProviderPrefix()
    {
        // The prefix stripped is the source's own provider only, not any leading slash-segment: a model id
        // that legitimately contains a slash keeps it, so it can still match a ProviderModelId that has one.
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("llama", "together", "meta-llama/llama-3")));

        var resolution = resolver.Resolve(Source, "meta-llama/llama-3", "together");

        Assert.Equal(new ResolvedModelIdentity("llama", "together"), resolution!.Value.Identity);
        Assert.Equal(ResolutionRung.Exact, resolution.Value.Rung);
    }

    [Fact]
    public void Resolve_DatedSnapshotSuffix_MatchesAtSnapshotSuffixStrippedRung()
    {
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("claude-sonnet-5", "anthropic", "claude-sonnet-4-5")));

        var resolution = resolver.Resolve(Source, "claude-sonnet-4-5-20250929", "anthropic");

        Assert.Equal(ResolutionRung.SnapshotSuffixStripped, resolution!.Value.Rung);
        Assert.True(resolution.Value.IsApproximate);
        Assert.Equal(new ResolvedModelIdentity("claude-sonnet-5", "anthropic"), resolution.Value.Identity);
    }

    [Fact]
    public void Resolve_VersionTierSuffix_MatchesAtVersionNormalizedRung()
    {
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        var resolution = resolver.Resolve(Source, "gpt-4o-latest", "openai");

        Assert.Equal(ResolutionRung.VersionNormalized, resolution!.Value.Rung);
        Assert.True(resolution.Value.IsApproximate);
    }

    [Fact]
    public void Resolve_OperatorOverride_TakesPrecedenceOverExactMatch()
    {
        using var db = new TempDatabase();
        var store = StoreWith(Entry("gpt-5.4", "openai", "gpt-4o"), Entry("custom-model", "openai", "big-pickle"));
        var overrideStore = db.CreateOverrideStore();
        overrideStore.Upsert(Source, "gpt-4o", "custom-model");
        var resolver = new ConfigModelIdentityResolver(store, overrideStore);

        var resolution = resolver.Resolve(Source, "gpt-4o", "openai");

        Assert.Equal(ResolutionRung.OperatorOverride, resolution!.Value.Rung);
        Assert.False(resolution.Value.IsApproximate);
        Assert.Equal(new ResolvedModelIdentity("custom-model", "openai"), resolution.Value.Identity);
    }

    [Fact]
    public void Resolve_OperatorOverride_IgnoredForOtherSources()
    {
        using var db = new TempDatabase();
        var store = StoreWith(Entry("gpt-5.4", "openai", "gpt-4o"), Entry("custom-model", "openai", "big-pickle"));
        var overrideStore = db.CreateOverrideStore();
        overrideStore.Upsert("OpenRouter", "gpt-4o", "custom-model");
        var resolver = new ConfigModelIdentityResolver(store, overrideStore);

        var resolution = resolver.Resolve(Source, "gpt-4o", "openai");

        Assert.Equal(ResolutionRung.Exact, resolution!.Value.Rung);
    }

    [Fact]
    public async Task Resolve_PicksUpConfigEdits_WithoutRebuildingResolver()
    {
        // Live reload: the resolver reads the current snapshot, so a model added at runtime resolves on the
        // next call without reconstructing anything - the version bump invalidates the cached index.
        var store = StoreWith(Entry("gpt-5.4", "openai", "gpt-4o"));
        var resolver = new ConfigModelIdentityResolver(store);

        Assert.Null(resolver.Resolve(Source, "claude-4", "anthropic"));

        await store.UpsertProviderAsync("anthropic", new ProviderOptions { BaseUrl = "https://example.com" }, TestContext.Current.CancellationToken);
        await store.UpsertModelAsync(Entry("claude-opus-4.6", "anthropic", "claude-4"), TestContext.Current.CancellationToken);

        var resolution = resolver.Resolve(Source, "claude-4", "anthropic");
        Assert.Equal(new ResolvedModelIdentity("claude-opus-4.6", "anthropic"), resolution!.Value.Identity);
    }
}
