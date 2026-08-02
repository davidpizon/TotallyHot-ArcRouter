using System.Diagnostics.CodeAnalysis;
using System.Text;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Resolves a client-facing model name to its configured upstream provider, mirroring LiteLLM's
/// <c>model_list</c> allowlist: only models present in configuration are routable.
/// </summary>
public interface IModelRouteResolver
{
    /// <summary>
    /// Attempts to resolve a client-facing model name to its upstream route.
    /// </summary>
    /// <param name="modelName">The model name from the request body.</param>
    /// <param name="route">The resolved route, when successful.</param>
    /// <returns><see langword="true"/> if the model is known and resolved; otherwise <see langword="false"/>.</returns>
    bool TryResolve(string? modelName, [NotNullWhen(true)] out ResolvedModelRoute? route);

    /// <summary>
    /// Lists the client-facing models this proxy is configured to route, in the order they appear in
    /// configuration. Used to answer the OpenAI-compatible model discovery endpoint (<c>GET /v1/models</c>)
    /// without forwarding the request to any single upstream provider.
    /// </summary>
    IReadOnlyList<AvailableModel> ListModels();

    /// <summary>
    /// Reports whether <paramref name="provider"/> is currently switched on (<see cref="ProviderOptions.Enabled"/>).
    /// Used to gate routing/fallback selection so a stopped provider is never routed to, the same way
    /// <see cref="ICircuitBreaker.IsProviderOpen"/> gates an unhealthy one.
    /// </summary>
    /// <param name="provider">The provider key to check.</param>
    /// <returns><see langword="true"/> if the provider is enabled or unknown; <see langword="false"/> if it is explicitly disabled.</returns>
    bool IsProviderEnabled(string provider);

    /// <summary>
    /// Reports whether <paramref name="modelName"/> is currently routable - both operator-started
    /// (<see cref="ModelRouteEntry.Enabled"/>) and reported present by the last "Refresh from endpoint"
    /// scan (<see cref="ModelRouteEntry.PresentUpstream"/>). The model-level twin of
    /// <see cref="IsProviderEnabled"/>, gating candidate selection the same way.
    /// </summary>
    /// <param name="modelName">The client-facing model name to check.</param>
    /// <returns>
    /// <see langword="true"/> if the model is routable or unknown; <see langword="false"/> if it is
    /// explicitly stopped or not currently reported by its provider's endpoint.
    /// </returns>
    bool IsModelEnabled(string modelName);
}

/// <summary>
/// A client-facing model this proxy is configured to route, along with the provider key it resolves to.
/// </summary>
/// <param name="ModelName">The client-facing model name, as it appears in <see cref="ModelRouteEntry.ModelName"/>.</param>
/// <param name="Provider">The provider key this model routes to.</param>
public sealed record AvailableModel(string ModelName, string Provider);

/// <summary>
/// A model resolved to a concrete upstream provider, along with the credentials to authenticate the forwarded request.
/// </summary>
/// <param name="ModelName">The client-facing model name that was resolved.</param>
/// <param name="Provider">The provider key the model routes to.</param>
/// <param name="ProviderModelId">The model identifier to send to the upstream provider.</param>
/// <param name="UpstreamBaseUrl">The absolute base URL of the upstream provider.</param>
/// <param name="AuthHeaderName">The HTTP header name used to carry the provider credential.</param>
/// <param name="AuthHeaderValue">The resolved credential header value, or <see langword="null"/> if no API key is configured/available.</param>
/// <param name="ExtraHeaders">Resolved custom headers (name/value) to add to the forwarded request, from the provider's configured <see cref="ProviderOptions.Headers"/>.</param>
/// <param name="IsFree">Whether this route's provider costs nothing (<see cref="ProviderOptions.IsFree"/>), making the request's cost a known zero rather than unknown.</param>
/// <param name="AwsRegion">The AWS region for an Amazon Bedrock route (<see cref="ProviderOptions.AwsRegion"/>); <see langword="null"/> for every non-Bedrock provider.</param>
/// <param name="AwsAccessKeyId">An explicit AWS access key id override for a Bedrock route, resolved from <see cref="ProviderOptions.AwsAccessKeyIdEnvVar"/>; <see langword="null"/> when not configured, meaning the Bedrock Runtime SDK client should use its own default AWS credential chain instead.</param>
/// <param name="AwsSecretAccessKey">The AWS secret access key paired with <see cref="AwsAccessKeyId"/>; always non-null exactly when <see cref="AwsAccessKeyId"/> is.</param>
/// <param name="AwsSessionToken">An optional AWS session token for temporary/STS credentials, paired with <see cref="AwsAccessKeyId"/>.</param>
/// <param name="EnableToolCallGuard">Whether this route's provider has the tool-call echo guard enabled (<see cref="ProviderOptions.EnableToolCallGuard"/>).</param>
public sealed record ResolvedModelRoute(
    string ModelName,
    string Provider,
    string ProviderModelId,
    Uri UpstreamBaseUrl,
    string AuthHeaderName,
    string? AuthHeaderValue,
    IReadOnlyList<KeyValuePair<string, string>> ExtraHeaders,
    bool IsFree = false,
    string? AwsRegion = null,
    string? AwsAccessKeyId = null,
    string? AwsSecretAccessKey = null,
    string? AwsSessionToken = null,
    bool EnableToolCallGuard = false)
{
    /// <summary>
    /// Overrides the compiler-generated member printer (the pattern the record feature recognizes: a
    /// method named exactly <c>PrintMembers</c> with this signature replaces the default one, which
    /// prints every property verbatim). Without this, <see cref="AuthHeaderValue"/>,
    /// <see cref="AwsSecretAccessKey"/>, <see cref="AwsSessionToken"/>, and any secret carried in
    /// <see cref="ExtraHeaders"/>' values would appear in this record's <c>ToString()</c> - and so in
    /// any log line, exception message, or debugger view that happens to include a
    /// <see cref="ResolvedModelRoute"/> - in plain text. <see cref="AwsAccessKeyId"/> is left visible:
    /// it identifies but does not itself authenticate (same judgment call as
    /// <c>BedrockRuntimeClientFactory.CredentialFingerprint</c>'s remarks).
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ModelName = ").Append(ModelName);
        builder.Append(", Provider = ").Append(Provider);
        builder.Append(", ProviderModelId = ").Append(ProviderModelId);
        builder.Append(", UpstreamBaseUrl = ").Append(UpstreamBaseUrl);
        builder.Append(", AuthHeaderName = ").Append(AuthHeaderName);
        builder.Append(", AuthHeaderValue = ").Append(Redacted(AuthHeaderValue));
        builder.Append(", ExtraHeaders = [").Append(string.Join(", ", ExtraHeaders.Select(h => $"{h.Key}=<redacted>"))).Append(']');
        builder.Append(", IsFree = ").Append(IsFree);
        builder.Append(", AwsRegion = ").Append(AwsRegion);
        builder.Append(", AwsAccessKeyId = ").Append(AwsAccessKeyId);
        builder.Append(", AwsSecretAccessKey = ").Append(Redacted(AwsSecretAccessKey));
        builder.Append(", AwsSessionToken = ").Append(Redacted(AwsSessionToken));
        builder.Append(", EnableToolCallGuard = ").Append(EnableToolCallGuard);
        return true;
    }

    /// <summary>Returns a safe placeholder for a secret value: "&lt;null&gt;" if absent, otherwise "&lt;redacted&gt;".</summary>
    private static string Redacted(string? secret) => secret is null ? "<null>" : "<redacted>";
}

/// <inheritdoc cref="IModelRouteResolver" />
public sealed class ModelRouteResolver : IModelRouteResolver
{
    private readonly IProviderConfigStore _store;
    private readonly IEnvironmentVariableProvider _environment;

    // Guards a rebuild of the cached lookup below when the store's snapshot version advances.
    private readonly object _rebuildLock = new();

    // The lookup + ordered list cached from the store snapshot that produced them. The reference fields
    // are volatile and always assigned a brand-new (never mutated) collection under _rebuildLock, so the
    // hot path (TryResolve on every proxied request) reads them without locking. _cachedVersion is
    // volatile so a fresh version written under the lock is visible to the lock-free fast-path check.
    private volatile Dictionary<string, (ModelRouteEntry Entry, ProviderOptions Provider)> _routes = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<ModelRouteEntry> _orderedEntries = [];
    private volatile Dictionary<string, bool> _providerEnabled = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, bool> _modelEnabled = new(StringComparer.OrdinalIgnoreCase);
    private volatile int _cachedVersion = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelRouteResolver"/> class.
    /// </summary>
    /// <param name="store">The writable provider-configuration store to resolve routes from.</param>
    /// <param name="environment">The environment variable accessor used to resolve provider API keys.</param>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">Thrown when the store's current configuration is inconsistent.</exception>
    public ModelRouteResolver(IProviderConfigStore store, IEnvironmentVariableProvider environment)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(environment);

        _store = store;
        _environment = environment;

        // Build eagerly so an invalid configuration surfaces at construction (startup), preserving the
        // previous fail-fast behavior rather than deferring the throw to the first request.
        EnsureCurrent();
    }

    /// <summary>
    /// Rebuilds the cached lookup from the store's current snapshot when its version has advanced since
    /// the last build. A no-op on the common path where configuration hasn't changed.
    /// </summary>
    private void EnsureCurrent()
    {
        var snapshot = _store.Snapshot;
        if (_cachedVersion == snapshot.Version)
        {
            return;
        }

        lock (_rebuildLock)
        {
            // Re-read under the lock: a concurrent caller may have already rebuilt to the latest version.
            snapshot = _store.Snapshot;
            if (_cachedVersion == snapshot.Version)
            {
                return;
            }

            var value = snapshot.Options;
            value.EnsureValid();

            _orderedEntries = value.ModelList;
            _routes = value.ModelList.ToDictionary(
                entry => entry.ModelName,
                entry => (entry, value.Providers[entry.Provider]),
                StringComparer.OrdinalIgnoreCase);
            _providerEnabled = value.Providers.ToDictionary(
                p => p.Key,
                p => p.Value.Enabled,
                StringComparer.OrdinalIgnoreCase);
            _modelEnabled = value.ModelList.ToDictionary(
                entry => entry.ModelName,
                entry => entry.Enabled && entry.PresentUpstream,
                StringComparer.OrdinalIgnoreCase);
            _cachedVersion = snapshot.Version;
        }
    }

    /// <inheritdoc />
    public bool TryResolve(string? modelName, [NotNullWhen(true)] out ResolvedModelRoute? route)
    {
        route = null;

        EnsureCurrent();

        if (string.IsNullOrWhiteSpace(modelName) || !_routes.TryGetValue(modelName, out var match))
        {
            return false;
        }

        var (entry, provider) = match;
        var authHeaderValue = ProviderCredentialResolver.BuildAuthHeaderValue(provider, _environment);
        var extraHeaders = ProviderCredentialResolver.ResolveExtraHeaders(provider, _environment);
        var (awsAccessKeyId, awsSecretAccessKey, awsSessionToken) = ProviderCredentialResolver.ResolveAwsCredentials(provider, _environment);

        route = new ResolvedModelRoute(
            entry.ModelName,
            entry.Provider,
            entry.ProviderModelId,
            new Uri(provider.BaseUrl, UriKind.Absolute),
            provider.AuthHeaderName,
            authHeaderValue,
            extraHeaders,
            provider.IsFree,
            provider.AwsRegion,
            awsAccessKeyId,
            awsSecretAccessKey,
            awsSessionToken,
            provider.EnableToolCallGuard);

        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<AvailableModel> ListModels()
    {
        EnsureCurrent();
        return _orderedEntries.Select(entry => new AvailableModel(entry.ModelName, entry.Provider)).ToList();
    }

    /// <inheritdoc />
    public bool IsProviderEnabled(string provider)
    {
        EnsureCurrent();
        return !_providerEnabled.TryGetValue(provider, out var enabled) || enabled;
    }

    /// <inheritdoc />
    public bool IsModelEnabled(string modelName)
    {
        EnsureCurrent();
        return !_modelEnabled.TryGetValue(modelName, out var enabled) || enabled;
    }
}

