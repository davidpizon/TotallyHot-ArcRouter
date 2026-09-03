using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Resolves a provider's custom headers - including authentication, expressed as an ordinary header - to
/// concrete name/value pairs to send upstream. Shared by <see cref="ModelRouteResolver"/> (per routed
/// request) and the management API's model-discovery endpoint (per provider), so both compute the
/// forwarded headers identically.
/// </summary>
internal static class ProviderCredentialResolver
{
    /// <summary>
    /// Resolves a provider's custom <see cref="ProviderOptions.Headers"/> to concrete name/value pairs to
    /// send upstream. Documented precedence: the header's literal <see cref="ProviderHeader.Value"/>, then
    /// the environment variable named by <see cref="ProviderHeader.ValueEnvVar"/>, then - when
    /// <paramref name="secretReader"/> is supplied - the protected-store entry named by
    /// <see cref="ProviderHeader.ValueSecretRef"/> (<c>docs/router/secrets-at-rest-plan.md</c> §5). Headers
    /// with an empty name, or whose value can't be resolved through any of the three, are skipped. Order is
    /// preserved. Shared by the forwarding path and the discovery endpoint so both send identical headers.
    /// </summary>
    /// <param name="provider">The provider whose custom headers to resolve.</param>
    /// <param name="environment">Accessor used to resolve env-var-sourced values.</param>
    /// <param name="secretReader">
    /// Optional reader for the protected secret store. Defaults to <see langword="null"/>, in which case a
    /// header whose value lives only in the store (neither a literal nor an env var) resolves to nothing -
    /// the same behavior as before <see cref="ProviderHeader.ValueSecretRef"/> existed.
    /// </param>
    public static IReadOnlyList<KeyValuePair<string, string>> ResolveExtraHeaders(
        ProviderOptions provider, IEnvironmentVariableProvider environment, ISecretReader? secretReader = null)
    {
        if (provider.Headers.Count == 0) return [];

        var resolved = new List<KeyValuePair<string, string>>(provider.Headers.Count);
        foreach (var header in provider.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name)) continue;

            var value = ResolveHeaderValue(header: header, environment: environment, secretReader: secretReader);
            if (value is not null) resolved.Add(new KeyValuePair<string, string>(key: header.Name, value: value));
        }

        return resolved;
    }

    /// <summary>
    /// Resolves a single header's value through the literal → env-var → secret-store precedence documented on
    /// <see cref="ResolveExtraHeaders"/>.
    /// </summary>
    private static string? ResolveHeaderValue(ProviderHeader header, IEnvironmentVariableProvider environment,
        ISecretReader? secretReader)
    {
        if (!string.IsNullOrWhiteSpace(header.Value)) return header.Value;

        if (!string.IsNullOrWhiteSpace(header.ValueEnvVar)) return environment.GetVariable(header.ValueEnvVar);

        if (!string.IsNullOrWhiteSpace(header.ValueSecretRef) && secretReader is not null
                                                              && secretReader.TryRead(name: header.ValueSecretRef,
                                                                  value: out var secretValue))
            return secretValue;

        return null;
    }

    /// <summary>
    /// Applies every resolvable custom header - including whichever one carries authentication - to an
    /// outbound probe request, exactly as the forwarding path applies them. Which is how a provider that
    /// needs an extra header to answer at all (Anthropic's <c>anthropic-version</c>) reaches every probe
    /// with no provider-specific code at the call site.
    /// </summary>
    /// <returns>
    /// The configured header names HTTP refused, or an empty list when all were accepted.
    /// <see cref="System.Net.Http.Headers.HttpHeaders.TryAddWithoutValidation(string, string?)"/> answers
    /// <see langword="false"/> for a name that is not a valid HTTP token rather than throwing, so a
    /// malformed name cannot break a caller's no-throw contract. What it can do is mislead: the auth header
    /// is silently omitted, the provider answers 401, and the caller blames the credential when the real
    /// fault is the header <em>name</em> in the provider's configuration. Returning the rejects lets the
    /// failure say so. Only names are returned - the values may be secrets.
    /// </returns>
    /// <param name="request">The request to apply headers to.</param>
    /// <param name="provider">The provider whose custom headers to apply.</param>
    /// <param name="environment">Accessor used to resolve env-var-sourced values.</param>
    /// <param name="secretReader">Optional reader for the protected secret store; see <see cref="ResolveExtraHeaders"/>.</param>
    public static IReadOnlyList<string> ApplyToRequest(
        HttpRequestMessage request,
        ProviderOptions provider,
        IEnvironmentVariableProvider environment,
        ISecretReader? secretReader = null)
    {
        List<string>? rejected = null;

        foreach (var (headerName, headerValue) in ResolveExtraHeaders(provider: provider, environment: environment,
                     secretReader: secretReader))
            if (!request.Headers.TryAddWithoutValidation(name: headerName, value: headerValue))
                (rejected ??= []).Add(headerName);

        return rejected ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Resolves an Amazon Bedrock provider's optional explicit AWS credential override
    /// (<see cref="ProviderOptions.AwsAccessKeyIdEnvVar"/>/<see cref="ProviderOptions.AwsSecretAccessKeyEnvVar"/>/
    /// <see cref="ProviderOptions.AwsSessionTokenEnvVar"/>), for <c>IBedrockRuntimeClientFactory</c>.
    /// Returns all-null when access key id or secret key isn't configured/resolvable, signaling "use the
    /// AWS SDK's own default credential chain" rather than a partially-resolved, broken override.
    /// </summary>
    public static (string? AccessKeyId, string? SecretAccessKey, string? SessionToken) ResolveAwsCredentials(
        ProviderOptions provider, IEnvironmentVariableProvider environment)
    {
        var accessKeyId = ResolveEnvVar(envVarName: provider.AwsAccessKeyIdEnvVar, environment: environment);
        var secretAccessKey = ResolveEnvVar(envVarName: provider.AwsSecretAccessKeyEnvVar, environment: environment);

        // Both halves are required; a blank one - the env var unset, or set to an empty/whitespace value -
        // means "not resolvable" and must fall back to the SDK's default credential chain rather than
        // produce a half-configured override that only fails at call time.
        if (accessKeyId is null || secretAccessKey is null) return (null, null, null);

        var sessionToken = ResolveEnvVar(envVarName: provider.AwsSessionTokenEnvVar, environment: environment);
        return (accessKeyId, secretAccessKey, sessionToken);

        static string? ResolveEnvVar(string? envVarName, IEnvironmentVariableProvider environment)
        {
            if (string.IsNullOrWhiteSpace(envVarName)) return null;

            var value = environment.GetVariable(envVarName);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}