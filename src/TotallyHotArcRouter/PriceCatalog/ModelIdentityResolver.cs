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
/// The ordered ladder of identity-resolution strategies, highest confidence first. Adapted from tokscale's
/// 8-step pricing resolution, trimmed to the rungs that apply to a router that owns its own model list:
/// tokscale's vendor-hardcoded and fuzzy word-boundary rungs are deliberately omitted (see
/// <c>docs/router/token-tracking-improvements.md</c> §5.7).
/// </summary>
public enum ResolutionRung
{
    /// <summary>An operator-authored override in the price-override store. Wins over everything.</summary>
    OperatorOverride,

    /// <summary>Provider and model id match exactly - the original <see cref="ConfigModelIdentityResolver"/> behavior.</summary>
    Exact,

    /// <summary>Matched after stripping a trailing dated-snapshot suffix (e.g. "-20250929").</summary>
    SnapshotSuffixStripped,

    /// <summary>Matched after normalizing a version/tier suffix (e.g. "-latest", "-preview", ":free").</summary>
    VersionNormalized,

    /// <summary>Matched on model id alone, ignoring a provider-name mismatch (e.g. azure-openai vs openai).</summary>
    ProviderAlias,
}

/// <summary>
/// A resolution attempt's outcome: the identity, plus which rung of the ladder produced it. The rung is
/// carried rather than discarded because it is what lets an approximate match be labeled approximate
/// downstream (see <see cref="Telemetry.CostConfidence"/>) instead of being indistinguishable from an exact
/// one - which is the actual objection <c>d3-alias-resolution.md</c> raises against fuzzy matching.
/// </summary>
/// <param name="Identity">The resolved client-facing identity.</param>
/// <param name="Rung">
/// Which rung matched. <see cref="ResolutionRung.Exact"/> and <see cref="ResolutionRung.OperatorOverride"/>
/// are authoritative; every lower rung marks the resulting price approximate (<see cref="IsApproximate"/>).
/// </param>
public readonly record struct IdentityResolution(ResolvedModelIdentity Identity, ResolutionRung Rung)
{
    /// <summary>
    /// Whether <see cref="Rung"/> is below the ladder's authoritative rungs, so the resulting price should
    /// be marked <see cref="Telemetry.CostConfidence.CatalogApproximate"/> rather than
    /// <see cref="Telemetry.CostConfidence.Catalog"/>.
    /// </summary>
    public bool IsApproximate => Rung is not (ResolutionRung.OperatorOverride or ResolutionRung.Exact);
}

/// <summary>
/// Resolves an aggregator's own <c>(model, provider)</c> naming onto the configured router identity via the
/// §5.7 resolution ladder: an operator override, then an exact match, then progressively looser automatic
/// rungs. A resolver that cannot match any rung returns <see langword="null"/>, and the caller stores the
/// price under the source's own keys unchanged - an unmatched model is left unresolved, never approximately
/// mapped without disclosure. See <c>docs/router/d3-alias-resolution.md</c> and
/// <c>docs/router/token-tracking-improvements.md</c> §5.7.
/// </summary>
public interface IModelIdentityResolver
{
    /// <summary>
    /// Maps an aggregator's own model/provider naming onto the configured router identity via the
    /// resolution ladder, or returns <see langword="null"/> when no rung matches.
    /// </summary>
    /// <param name="sourceName">The aggregator source name (e.g. <c>LiteLLM</c>), used for operator-override lookup.</param>
    /// <param name="aggregatorModelId">The source's own model key, e.g. <c>gpt-4o</c> or <c>openai/gpt-4o</c>.</param>
    /// <param name="aggregatorProvider">The source's own provider string, e.g. <c>openai</c>.</param>
    IdentityResolution? Resolve(string sourceName, string aggregatorModelId, string aggregatorProvider);
}

/// <summary>
/// <see cref="IModelIdentityResolver"/> backed by the live <see cref="IProviderConfigStore"/> and an optional
/// <see cref="ModelAliasOverrideStore"/>. Tries each <see cref="ResolutionRung"/> in order and returns the
/// first that matches; see the ladder's own remarks for why no fuzzy rung follows <see cref="ResolutionRung.ProviderAlias"/>.
/// </summary>
public sealed class ConfigModelIdentityResolver : IModelIdentityResolver
{
    // The normalization stages live in ModelNameCanonicalizer so this resolver and the CodeRouterBench
    // loader share one implementation. They are applied here one at a time rather than through
    // ModelNameCanonicalizer.Canonicalize on purpose: each rung below is defined by *which* stage it
    // needed, and that is what marks the resulting price approximate. Canonicalize would also unify
    // version separators, which would manufacture matches this ladder currently (and deliberately) misses.
    private readonly IProviderConfigStore _configStore;
    private readonly ModelAliasOverrideStore? _overrideStore;

    // The resolved index is rebuilt only when the config's version changes, so a poll cycle's hundreds of
    // Resolve calls share one build rather than re-scanning ModelList each time. Guarded because ingestion
    // (which calls Resolve) and a runtime config edit (which bumps the version) run on different threads.
    private readonly object _sync = new();
    private int _cachedVersion = -1;
    private ResolvedIndex _index = new([], [], [], [], []);

    /// <param name="configStore">The live routing-configuration source the alias index is derived from.</param>
    /// <param name="overrideStore">
    /// Optional operator-override store consulted first (<see cref="ResolutionRung.OperatorOverride"/>).
    /// When <see langword="null"/>, that rung is skipped and resolution starts at <see cref="ResolutionRung.Exact"/>.
    /// </param>
    public ConfigModelIdentityResolver(IProviderConfigStore configStore, ModelAliasOverrideStore? overrideStore = null)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        _configStore = configStore;
        _overrideStore = overrideStore;
    }

    /// <inheritdoc />
    public IdentityResolution? Resolve(string sourceName, string aggregatorModelId, string aggregatorProvider)
    {
        if (string.IsNullOrWhiteSpace(aggregatorModelId) || string.IsNullOrWhiteSpace(aggregatorProvider))
        {
            return null;
        }

        var index = GetIndex();

        if (!string.IsNullOrWhiteSpace(sourceName) && _overrideStore is not null)
        {
            var overriddenModelName = _overrideStore.TryGetModelName(sourceName, aggregatorModelId);
            if (overriddenModelName is not null && index.ByModelName.TryGetValue(overriddenModelName, out var overriddenIdentity))
            {
                return new IdentityResolution(overriddenIdentity, ResolutionRung.OperatorOverride);
            }
        }

        var provider = ModelNameCanonicalizer.NormalizeProvider(aggregatorProvider);
        var modelId = ModelNameCanonicalizer.NormalizeBase(aggregatorModelId, aggregatorProvider);

        if (index.ByProviderAndModelId.TryGetValue((provider, modelId), out var exact))
        {
            return new IdentityResolution(exact, ResolutionRung.Exact);
        }

        var snapshotStripped = ModelNameCanonicalizer.StripSnapshotSuffix(modelId);
        if (snapshotStripped != modelId && index.ByProviderAndStrippedModelId.TryGetValue((provider, snapshotStripped), out var snapshotMatch))
        {
            return new IdentityResolution(snapshotMatch, ResolutionRung.SnapshotSuffixStripped);
        }

        var versionNormalized = ModelNameCanonicalizer.StripVersionSuffix(snapshotStripped);
        if (versionNormalized != modelId && index.ByProviderAndVersionNormalizedModelId.TryGetValue((provider, versionNormalized), out var versionMatch))
        {
            return new IdentityResolution(versionMatch, ResolutionRung.VersionNormalized);
        }

        // Provider-alias rung: match on model id alone, across every configured provider, ignoring the
        // aggregator's stated provider entirely - the resolved identity still carries the *configured*
        // provider (never the aggregator's), so a genuine cross-provider naming collision (rare, since
        // ProviderModelId values are host-specific) resolves to whichever configured entry matched first.
        if (index.ByModelIdAnyProvider.TryGetValue(versionNormalized, out var aliasMatch) ||
            index.ByModelIdAnyProvider.TryGetValue(modelId, out aliasMatch))
        {
            return new IdentityResolution(aliasMatch, ResolutionRung.ProviderAlias);
        }

        return null;
    }

    private ResolvedIndex GetIndex()
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

    private static ResolvedIndex Build(ModelRoutingOptions options)
    {
        var byModelName = new Dictionary<string, ResolvedModelIdentity>(StringComparer.OrdinalIgnoreCase);
        var byProviderAndModelId = new Dictionary<(string Provider, string ModelId), ResolvedModelIdentity>();
        var byProviderAndStripped = new Dictionary<(string Provider, string ModelId), ResolvedModelIdentity>();
        var byProviderAndVersionNormalized = new Dictionary<(string Provider, string ModelId), ResolvedModelIdentity>();
        var byModelIdAnyProvider = new Dictionary<string, ResolvedModelIdentity>();

        foreach (var entry in options.ModelList)
        {
            if (string.IsNullOrWhiteSpace(entry.Provider) || string.IsNullOrWhiteSpace(entry.ProviderModelId))
            {
                continue;
            }

            var identity = new ResolvedModelIdentity(entry.ModelName, entry.Provider);
            var provider = ModelNameCanonicalizer.NormalizeProvider(entry.Provider);
            var modelId = ModelNameCanonicalizer.NormalizeBase(entry.ProviderModelId, entry.Provider);

            // First entry wins on a duplicate key: two ModelNames mapping to one ambiguous cell should not
            // let the later one silently overwrite. ModelName itself is already unique (ModelRoutingOptions.EnsureValid).
            byModelName.TryAdd(entry.ModelName, identity);
            byProviderAndModelId.TryAdd((provider, modelId), identity);

            var stripped = ModelNameCanonicalizer.StripSnapshotSuffix(modelId);
            byProviderAndStripped.TryAdd((provider, stripped), identity);

            var versionNormalized = ModelNameCanonicalizer.StripVersionSuffix(stripped);
            byProviderAndVersionNormalized.TryAdd((provider, versionNormalized), identity);
            byModelIdAnyProvider.TryAdd(versionNormalized, identity);
            byModelIdAnyProvider.TryAdd(modelId, identity);
        }

        return new ResolvedIndex(byModelName, byProviderAndModelId, byProviderAndStripped, byProviderAndVersionNormalized, byModelIdAnyProvider);
    }

    /// <summary>The rebuildable indexes <see cref="Build"/> derives from <see cref="ModelRoutingOptions"/>, one per resolution rung.</summary>
    private sealed record ResolvedIndex(
        Dictionary<string, ResolvedModelIdentity> ByModelName,
        Dictionary<(string Provider, string ModelId), ResolvedModelIdentity> ByProviderAndModelId,
        Dictionary<(string Provider, string ModelId), ResolvedModelIdentity> ByProviderAndStrippedModelId,
        Dictionary<(string Provider, string ModelId), ResolvedModelIdentity> ByProviderAndVersionNormalizedModelId,
        Dictionary<string, ResolvedModelIdentity> ByModelIdAnyProvider);
}
