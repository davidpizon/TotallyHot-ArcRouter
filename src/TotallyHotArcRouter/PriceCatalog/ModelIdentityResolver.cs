using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// The client-facing identity a price row is stored under once an aggregator's own naming has been
/// resolved onto the router's configuration: the <c>(ModelName, Provider)</c> pair from
/// <c>ModelRouting:ModelList</c>. See <c>docs/router/d3-alias-resolution.md</c>.
/// </summary>
/// <param name="ModelName">The client-facing <c>ModelRouting:ModelList[].ModelName</c>.</param>
/// <param name="Provider">The configured <c>ModelRouting:Providers</c> key that serves it.</param>
public readonly record struct ResolvedModelIdentity(string ModelName, string Provider);

/// <summary>
/// Resolves an aggregator's own <c>(model, provider)</c> naming onto the configured router identity (D3),
/// so a price ingested from LiteLLM (<c>gpt-4o</c>) and one from OpenRouter (<c>openai/gpt-4o</c>) for the
/// same real model land in a single <c>(model, provider)</c> cell keyed by the operator's
/// <c>ModelName</c> - which is what the runtime cost lookup queries. A resolver that cannot match returns
/// <see langword="null"/>, and the caller stores the price under the source's own keys unchanged: an
/// unmatched model is left unresolved, never approximately mapped. See
/// <c>docs/router/d3-alias-resolution.md</c>.
/// </summary>
public interface IModelIdentityResolver
{
    /// <summary>
    /// Maps an aggregator's own model/provider naming onto the configured router identity, or returns
    /// <see langword="null"/> when no configured model matches exactly.
    /// </summary>
    /// <param name="aggregatorModelId">The source's own model key, e.g. <c>gpt-4o</c> or <c>openai/gpt-4o</c>.</param>
    /// <param name="aggregatorProvider">The source's own provider string, e.g. <c>openai</c>.</param>
    ResolvedModelIdentity? Resolve(string aggregatorModelId, string aggregatorProvider);
}

/// <summary>
/// <see cref="IModelIdentityResolver"/> backed by the live <see cref="IProviderConfigStore"/>. Matches an
/// aggregator row against a <c>ModelRouting:ModelList</c> entry when the provider names agree
/// (case-insensitively) and the aggregator's model id, after stripping its own <c>provider/</c> prefix,
/// equals the entry's <c>ProviderModelId</c>. The match is deliberately exact - no fuzzy or best-guess
/// matching - because a confidently wrong price is the failure the whole price subsystem exists to prevent
/// (see <c>docs/router/d3-alias-resolution.md</c>). Provider-name divergence (config <c>azure-openai</c>
/// vs aggregator <c>openai</c>) and pinned <c>ProviderModelId</c>s are left for the explicit-override slice,
/// not guessed here.
/// </summary>
public sealed class ConfigModelIdentityResolver : IModelIdentityResolver
{
    private readonly IProviderConfigStore _configStore;

    // The resolved index is rebuilt only when the config's version changes, so a poll cycle's hundreds of
    // Resolve calls share one build rather than re-scanning ModelList each time. Guarded because ingestion
    // (which calls Resolve) and a runtime config edit (which bumps the version) run on different threads.
    private readonly object _sync = new();
    private int _cachedVersion = -1;
    private Dictionary<(string Provider, string ModelId), ResolvedModelIdentity> _index = new();

    /// <param name="configStore">The live routing-configuration source the alias index is derived from.</param>
    public ConfigModelIdentityResolver(IProviderConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        _configStore = configStore;
    }

    /// <inheritdoc />
    public ResolvedModelIdentity? Resolve(string aggregatorModelId, string aggregatorProvider)
    {
        if (string.IsNullOrWhiteSpace(aggregatorModelId) || string.IsNullOrWhiteSpace(aggregatorProvider))
        {
            return null;
        }

        var index = GetIndex();
        var key = (NormalizeProvider(aggregatorProvider), NormalizeModelId(aggregatorModelId, aggregatorProvider));
        return index.TryGetValue(key, out var identity) ? identity : null;
    }

    private Dictionary<(string Provider, string ModelId), ResolvedModelIdentity> GetIndex()
    {
        var snapshot = _configStore.Snapshot;
        lock (_sync)
        {
            if (snapshot.Version != _cachedVersion)
            {
                _index = Build(snapshot.Options);
                _cachedVersion = snapshot.Version;
            }

            return _index;
        }
    }

    private static Dictionary<(string Provider, string ModelId), ResolvedModelIdentity> Build(ModelRoutingOptions options)
    {
        var index = new Dictionary<(string, string), ResolvedModelIdentity>();

        foreach (var entry in options.ModelList)
        {
            if (string.IsNullOrWhiteSpace(entry.Provider) || string.IsNullOrWhiteSpace(entry.ProviderModelId))
            {
                continue;
            }

            var key = (NormalizeProvider(entry.Provider), NormalizeModelId(entry.ProviderModelId, entry.Provider));

            // First entry wins on a duplicate (provider, providerModelId): two ModelNames mapping to one
            // provider cell is ambiguous, so decline to guess which the aggregator meant rather than let the
            // later one silently overwrite. ModelName itself is already unique (ModelRoutingOptions.EnsureValid).
            index.TryAdd(key, new ResolvedModelIdentity(entry.ModelName, entry.Provider));
        }

        return index;
    }

    /// <summary>
    /// Normalizes a provider name for use as a dictionary key by trimming whitespace and lowercasing.
    /// </summary>
    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant();

    // Strips the aggregator's own "provider/" prefix when present (OpenRouter's "openai/gpt-4o" -> "gpt-4o";
    // LiteLLM's bare "gpt-4o" is unchanged), then lowercases. Only the source's own provider prefix is
    // stripped - not any leading slash-segment - so a model id that legitimately contains a slash is not
    // mangled.
    /// <summary>
    /// Normalizes a model id for use as a dictionary key: strips the source's own "provider/" prefix if
    /// present, then lowercases the result.
    /// </summary>
    private static string NormalizeModelId(string modelId, string provider)
    {
        var trimmed = modelId.Trim();
        var prefix = provider.Trim() + "/";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }

        return trimmed.ToLowerInvariant();
    }
}

