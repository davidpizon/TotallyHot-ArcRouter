using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Tests.Proxy;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="ConfigModelIdentityResolver"/>'s D3 alias resolution: mapping each aggregator's own
/// model/provider naming onto the configured <c>ModelRouting:ModelList</c> identity.
/// </summary>
public class ConfigModelIdentityResolverTests
{
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
    public void Resolve_LiteLlmBareName_ResolvesToConfiguredIdentity()
    {
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        var identity = resolver.Resolve("gpt-4o", "openai");

        Assert.Equal(new ResolvedModelIdentity("gpt-5.4", "openai"), identity);
    }

    [Fact]
    public void Resolve_OpenRouterPrefixedId_StripsProviderPrefixAndResolves()
    {
        // OpenRouter names the same model "openai/gpt-4o"; its provider prefix is stripped so it maps onto the
        // same configured entry LiteLLM's "gpt-4o" does - which is exactly what makes the two collide into one
        // (model, provider) cell at ingest.
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        var identity = resolver.Resolve("openai/gpt-4o", "openai");

        Assert.Equal(new ResolvedModelIdentity("gpt-5.4", "openai"), identity);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive_OnBothProviderAndModelId()
    {
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        Assert.Equal(new ResolvedModelIdentity("gpt-5.4", "openai"), resolver.Resolve("GPT-4O", "OpenAI"));
    }

    [Fact]
    public void Resolve_ProviderMismatch_ReturnsNull()
    {
        // Same model id, different provider: not a match. The same real model on a different host is a
        // different priced cell (D7), and a provider name the config doesn't use is left for an explicit
        // override, never guessed.
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        Assert.Null(resolver.Resolve("gpt-4o", "anthropic"));
    }

    [Fact]
    public void Resolve_UnknownModelId_ReturnsNull()
    {
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("gpt-5.4", "openai", "gpt-4o")));

        Assert.Null(resolver.Resolve("some-model-nobody-configured", "openai"));
    }

    [Fact]
    public void Resolve_DoesNotStripSlashThatIsNotTheProviderPrefix()
    {
        // The prefix stripped is the source's own provider only, not any leading slash-segment: a model id
        // that legitimately contains a slash keeps it, so it can still match a ProviderModelId that has one.
        var resolver = new ConfigModelIdentityResolver(StoreWith(Entry("llama", "together", "meta-llama/llama-3")));

        Assert.Equal(new ResolvedModelIdentity("llama", "together"), resolver.Resolve("meta-llama/llama-3", "together"));
    }

    [Fact]
    public async Task Resolve_PicksUpConfigEdits_WithoutRebuildingResolver()
    {
        // Live reload: the resolver reads the current snapshot, so a model added at runtime resolves on the
        // next call without reconstructing anything - the version bump invalidates the cached index.
        var store = StoreWith(Entry("gpt-5.4", "openai", "gpt-4o"));
        var resolver = new ConfigModelIdentityResolver(store);

        Assert.Null(resolver.Resolve("claude-4", "anthropic"));

        await store.UpsertProviderAsync("anthropic", new ProviderOptions { BaseUrl = "https://example.com" }, TestContext.Current.CancellationToken);
        await store.UpsertModelAsync(Entry("claude-opus-4.6", "anthropic", "claude-4"), TestContext.Current.CancellationToken);

        Assert.Equal(new ResolvedModelIdentity("claude-opus-4.6", "anthropic"), resolver.Resolve("claude-4", "anthropic"));
    }
}

