using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="ProviderConfigStore"/>: first-run seeding (in memory, no file written until an
/// edit), load-from-file precedence, validated persistence, atomic version bumps, provider/model
/// mutators, and concurrent-write safety.
/// </summary>
public sealed class ProviderConfigStoreTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(path1: Path.GetTempPath(), path2: $"model-routing-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    private static ModelRoutingOptions SeedOptions(string providerKey = "openai", string modelName = "gpt-5.4")
    {
        return new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [providerKey] = new() { BaseUrl = "https://api.openai.com", AuthHeaderName = "Authorization" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = modelName, Provider = providerKey, ProviderModelId = modelName }
            ]
        };
    }

    private ProviderConfigStore CreateStore(ModelRoutingOptions seed)
    {
        return new ProviderConfigStore(
            logger: Mock.Of<ILogger<ProviderConfigStore>>(),
            seed: Options.Create(seed),
            options: Options.Create(new ProviderConfigStoreOptions { FilePath = _tempPath }));
    }

    [Fact]
    public void Constructor_NoFile_SeedsFromOptionsInMemory_WithoutWritingFile()
    {
        var store = CreateStore(SeedOptions());

        Assert.Equal(0, actual: store.Snapshot.Version);
        Assert.True(store.Snapshot.Options.Providers.ContainsKey("openai"));
        Assert.Single(store.Snapshot.Options.ModelList);
        // Seeding must not touch disk: an unreconfigured proxy leaves no file behind.
        Assert.False(File.Exists(_tempPath));
    }

    [Fact]
    public void Constructor_FileExists_LoadsFromFile_AndIgnoresSeed()
    {
        // A persisted file with "anthropic" wins over a seed that only knows "openai".
        var persisted = SeedOptions(providerKey: "anthropic", modelName: "claude-opus-4.6");
        File.WriteAllText(path: _tempPath,
            contents: JsonSerializer.Serialize(value: persisted,
                options: new JsonSerializerOptions { WriteIndented = true }));

        var store = CreateStore(SeedOptions(providerKey: "openai", modelName: "gpt-5.4"));

        Assert.True(store.Snapshot.Options.Providers.ContainsKey("anthropic"));
        Assert.False(store.Snapshot.Options.Providers.ContainsKey("openai"));
        Assert.Equal(expected: "claude-opus-4.6", actual: store.Snapshot.Options.ModelList.Single().ModelName);
    }

    [Fact]
    public async Task ReplaceAsync_RoundTripsIsFree_ThroughTheFile()
    {
        var seed = SeedOptions(providerKey: "ollama", modelName: "llama3");
        var store = CreateStore(seed);

        await store.ReplaceAsync(next: new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["ollama"] = new() { BaseUrl = "http://localhost:11434/v1", IsFree = true }
            },
            ModelList = seed.ModelList
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(new ProviderConfigStore(
                logger: Mock.Of<ILogger<ProviderConfigStore>>(),
                seed: Options.Create(SeedOptions()),
                options: Options.Create(new ProviderConfigStoreOptions { FilePath = _tempPath }))
            .Snapshot.Options.Providers["ollama"].IsFree);
    }

    // IsFree is a new key: every model-routing.json written before it existed lacks it. Those files must
    // keep loading, with the provider defaulting to paid - no migration, and no provider silently
    // becoming free because a key was absent.
    [Fact]
    public void Constructor_FileWrittenWithoutIsFree_LoadsProviderAsNotFree()
    {
        File.WriteAllText(path: _tempPath, """
                                           {
                                             "Providers": { "openai": { "BaseUrl": "https://api.openai.com", "AuthHeaderName": "Authorization" } },
                                             "ModelList": [ { "ModelName": "gpt-5.4", "Provider": "openai", "ProviderModelId": "gpt-5.4" } ]
                                           }
                                           """);

        var store = CreateStore(SeedOptions());

        Assert.False(store.Snapshot.Options.Providers["openai"].IsFree);
    }

    // ModelRouteEntry.Enabled/PresentUpstream are new keys too, with the same load-bearing default-true
    // rationale as ProviderOptions.Enabled: a model-routing.json written before either existed has neither
    // key on any ModelList entry, and both must come back routable rather than silently stopped/absent.
    [Fact]
    public void Constructor_FileWrittenWithoutEnabledOrPresentUpstream_LoadsModelAsEnabledAndPresent()
    {
        File.WriteAllText(path: _tempPath, """
                                           {
                                             "Providers": { "openai": { "BaseUrl": "https://api.openai.com", "AuthHeaderName": "Authorization" } },
                                             "ModelList": [ { "ModelName": "gpt-5.4", "Provider": "openai", "ProviderModelId": "gpt-5.4" } ]
                                           }
                                           """);

        var store = CreateStore(SeedOptions());

        var model = store.Snapshot.Options.ModelList.Single();
        Assert.True(model.Enabled);
        Assert.True(model.PresentUpstream);
    }

    [Fact]
    public void Constructor_LoadedProvidersDictionary_IsCaseInsensitive()
    {
        File.WriteAllText(path: _tempPath,
            contents: JsonSerializer.Serialize(value: SeedOptions(),
                options: new JsonSerializerOptions { WriteIndented = true }));

        var store = CreateStore(SeedOptions());

        // System.Text.Json loses the case-insensitive comparer on deserialize; Normalize restores it.
        Assert.True(store.Snapshot.Options.Providers.ContainsKey("OPENAI"));
    }

    [Fact]
    public async Task ReplaceAsync_PersistsToFile_BumpsVersion_AndReloadsOnNewStore()
    {
        var store = CreateStore(SeedOptions());

        var next = SeedOptions(providerKey: "moonshot", modelName: "kimi-k2.5");
        await store.ReplaceAsync(next: next, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: store.Snapshot.Version);
        Assert.True(store.Snapshot.Options.Providers.ContainsKey("moonshot"));
        Assert.True(File.Exists(_tempPath));

        // A fresh store over the same path must load the persisted config (starting again at version 0).
        var reloaded = CreateStore(SeedOptions());
        Assert.Equal(0, actual: reloaded.Snapshot.Version);
        Assert.True(reloaded.Snapshot.Options.Providers.ContainsKey("moonshot"));
    }

    [Fact]
    public async Task ReplaceAsync_InvalidConfiguration_Throws_AndDoesNotPersistOrAdvance()
    {
        var store = CreateStore(SeedOptions());

        var invalid = new ModelRoutingOptions
        {
            Providers = [with(StringComparer.OrdinalIgnoreCase)],
            // Model references a provider that doesn't exist -> EnsureValid rejects it.
            ModelList = [new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "nope", ProviderModelId = "gpt-5.4" }]
        };

        await Assert.ThrowsAsync<OptionsValidationException>(() =>
            store.ReplaceAsync(next: invalid, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, actual: store.Snapshot.Version);
        Assert.True(store.Snapshot.Options.Providers.ContainsKey("openai"));
        Assert.False(File.Exists(_tempPath));
    }

    [Fact]
    public async Task UpsertProviderAsync_AddsProvider_KeepingModels()
    {
        var store = CreateStore(SeedOptions());

        await store.UpsertProviderAsync(
            key: "ollama",
            provider: new ProviderOptions { BaseUrl = "http://localhost:11434/v1", AuthHeaderName = "Authorization" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(store.Snapshot.Options.Providers.ContainsKey("ollama"));
        Assert.True(store.Snapshot.Options.Providers.ContainsKey("openai"));
        Assert.Single(store.Snapshot.Options.ModelList);
    }

    [Fact]
    public async Task RemoveProviderAsync_StillReferencedByModel_CascadesToItsModels()
    {
        var store = CreateStore(SeedOptions());

        // "openai" still has the "gpt-5.4" model pointing at it. Removal cascades rather than being
        // rejected: leaving that model behind would strand it on a provider that no longer exists.
        await store.RemoveProviderAsync(key: "openai", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(store.Snapshot.Options.Providers.ContainsKey("openai"));
        Assert.DoesNotContain(collection: store.Snapshot.Options.ModelList, filter: m => m.ModelName == "gpt-5.4");
    }

    [Fact]
    public async Task RemoveProviderAsync_LeavesOtherProvidersModelsIntact()
    {
        // Two providers, two models each, so a cascade that over-reaches is visible.
        var seed = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { BaseUrl = "https://api.openai.com" },
                ["ollama"] = new() { BaseUrl = "http://localhost:11434/v1" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "gpt-5.4", Provider = "openai", ProviderModelId = "gpt-5.4" },
                new ModelRouteEntry { ModelName = "llama3", Provider = "ollama", ProviderModelId = "llama3" },
                new ModelRouteEntry { ModelName = "gpt-4o", Provider = "openai", ProviderModelId = "gpt-4o" },
                new ModelRouteEntry { ModelName = "mistral", Provider = "ollama", ProviderModelId = "mistral" }
            ]
        };
        var store = CreateStore(seed);

        await store.RemoveProviderAsync(key: "openai", cancellationToken: TestContext.Current.CancellationToken);

        // The cascade is scoped to the removed provider - the other provider's models survive in order.
        Assert.False(store.Snapshot.Options.Providers.ContainsKey("openai"));
        Assert.True(store.Snapshot.Options.Providers.ContainsKey("ollama"));
        Assert.Equal(expected: ["llama3", "mistral"],
            actual: store.Snapshot.Options.ModelList.Select(m => m.ModelName).ToList());
    }

    [Fact]
    public async Task UpsertModelAsync_EditingExistingModel_PreservesPosition()
    {
        var seed = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new() { BaseUrl = "https://example.com", AuthHeaderName = "Authorization" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "a", Provider = "p", ProviderModelId = "a" },
                new ModelRouteEntry { ModelName = "b", Provider = "p", ProviderModelId = "b" },
                new ModelRouteEntry { ModelName = "c", Provider = "p", ProviderModelId = "c" }
            ]
        };
        var store = CreateStore(seed);

        await store.UpsertModelAsync(
            entry: new ModelRouteEntry { ModelName = "b", Provider = "p", ProviderModelId = "b-updated" },
            cancellationToken: TestContext.Current.CancellationToken);

        var models = store.Snapshot.Options.ModelList;
        Assert.Equal(expected: ["a", "b", "c"], actual: models.Select(m => m.ModelName));
        Assert.Equal(expected: "b-updated", actual: models.Single(m => m.ModelName == "b").ProviderModelId);
    }

    [Fact]
    public async Task RemoveModelAsync_RemovesOnlyThatModel()
    {
        var seed = new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = new() { BaseUrl = "https://example.com", AuthHeaderName = "Authorization" }
            },
            ModelList =
            [
                new ModelRouteEntry { ModelName = "a", Provider = "p", ProviderModelId = "a" },
                new ModelRouteEntry { ModelName = "b", Provider = "p", ProviderModelId = "b" }
            ]
        };
        var store = CreateStore(seed);

        await store.RemoveModelAsync(modelName: "a", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: ["b"], actual: store.Snapshot.Options.ModelList.Select(m => m.ModelName));
    }

    [Fact]
    public async Task ReplaceAsync_ConcurrentEdits_ProduceValidFileAndSequentialVersions()
    {
        var store = CreateStore(SeedOptions());
        const int writers = 20;

        var tasks = Enumerable.Range(0, count: writers).Select(i => store.ReplaceAsync(
            next: SeedOptions(providerKey: $"p{i}", modelName: $"m{i}"),
            cancellationToken: TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        // Each edit bumps the version by exactly one under the write mutex, so N edits -> version N.
        Assert.Equal(expected: writers, actual: store.Snapshot.Version);

        // The file must be intact (not a half-interleaved write): it deserializes to a valid config.
        var json = await File.ReadAllTextAsync(path: _tempPath,
            cancellationToken: TestContext.Current.CancellationToken);
        var reloaded = JsonSerializer.Deserialize<ModelRoutingOptions>(json);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded.Providers);
        Assert.Single(reloaded.ModelList);
    }

    [Fact]
    public async Task ReplaceAsync_RaisesChanged()
    {
        var store = CreateStore(SeedOptions());
        var raised = 0;
        store.Changed += () => Interlocked.Increment(ref raised);

        await store.ReplaceAsync(next: SeedOptions(providerKey: "zhipu", modelName: "glm-5"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, actual: raised);
    }
}