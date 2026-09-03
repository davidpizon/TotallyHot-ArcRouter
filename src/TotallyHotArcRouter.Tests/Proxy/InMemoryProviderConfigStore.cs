using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// A file-free <see cref="IProviderConfigStore"/> for tests: holds the configuration snapshot in
/// memory (no disk persistence), validating and version-bumping on every edit exactly as the real
/// <see cref="ProviderConfigStore"/> does. Lets resolver/interceptor tests exercise the store-backed
/// resolver (including live reload) without touching the file system.
/// </summary>
internal sealed class InMemoryProviderConfigStore : IProviderConfigStore
{
    private volatile ProviderConfigSnapshot _snapshot;

    public InMemoryProviderConfigStore(ModelRoutingOptions options)
    {
        var normalized = Normalize(options);
        normalized.EnsureValid();
        _snapshot = new ProviderConfigSnapshot(Options: normalized, 0);
    }

    public ProviderConfigSnapshot Snapshot => _snapshot;

    public event Action? Changed;

    public Task ReplaceAsync(ModelRoutingOptions next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        var normalized = Normalize(next);
        normalized.EnsureValid();
        _snapshot = new ProviderConfigSnapshot(Options: normalized, Version: _snapshot.Version + 1);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task UpsertProviderAsync(string key, ProviderOptions provider, CancellationToken cancellationToken = default)
    {
        // Mirror ProviderConfigStore's argument guards so the double rejects the same bad input.
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(provider);
        var current = _snapshot.Options;
        var providers =
            new Dictionary<string, ProviderOptions>(dictionary: current.Providers,
                comparer: StringComparer.OrdinalIgnoreCase)
            {
                [key] = provider
            };
        return ReplaceAsync(next: new ModelRoutingOptions { Providers = providers, ModelList = [.. current.ModelList] },
            cancellationToken: cancellationToken);
    }

    public Task RemoveProviderAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var current = _snapshot.Options;
        var providers = new Dictionary<string, ProviderOptions>(dictionary: current.Providers,
            comparer: StringComparer.OrdinalIgnoreCase);
        providers.Remove(key);
        // Mirror ProviderConfigStore's cascade: the provider's models go with it, in the same edit.
        var models = current.ModelList
            .Where(m => !string.Equals(a: m.Provider, b: key, comparisonType: StringComparison.OrdinalIgnoreCase))
            .ToList();
        return ReplaceAsync(next: new ModelRoutingOptions { Providers = providers, ModelList = models },
            cancellationToken: cancellationToken);
    }

    public Task UpsertModelAsync(ModelRouteEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var current = _snapshot.Options;
        var replaced = false;
        var models = current.ModelList
            .Select(m =>
            {
                if (string.Equals(a: m.ModelName, b: entry.ModelName,
                        comparisonType: StringComparison.OrdinalIgnoreCase))
                {
                    replaced = true;
                    return entry;
                }

                return m;
            })
            .ToList();

        if (!replaced) models.Add(entry);

        return ReplaceAsync(
            next: new ModelRoutingOptions
            {
                Providers = new Dictionary<string, ProviderOptions>(dictionary: current.Providers,
                    comparer: StringComparer.OrdinalIgnoreCase),
                ModelList = models
            },
            cancellationToken: cancellationToken);
    }

    public Task RemoveModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var current = _snapshot.Options;
        var models = current.ModelList
            .Where(m => !string.Equals(a: m.ModelName, b: modelName,
                comparisonType: StringComparison.OrdinalIgnoreCase))
            .ToList();

        return ReplaceAsync(
            next: new ModelRoutingOptions
            {
                Providers = new Dictionary<string, ProviderOptions>(dictionary: current.Providers,
                    comparer: StringComparer.OrdinalIgnoreCase),
                ModelList = models
            },
            cancellationToken: cancellationToken);
    }

    private static ModelRoutingOptions Normalize(ModelRoutingOptions options)
    {
        return new ModelRoutingOptions
        {
            Providers = options.Providers is null
                ? new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ProviderOptions>(dictionary: options.Providers,
                    comparer: StringComparer.OrdinalIgnoreCase),
            ModelList = options.ModelList is null ? [] : [.. options.ModelList]
        };
    }
}