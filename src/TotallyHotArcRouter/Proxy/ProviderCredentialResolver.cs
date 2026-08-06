using TotallyHot.ArcRouter.Models;

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
    /// send upstream, each value taken from the header's literal <see cref="ProviderHeader.Value"/> or, when
    /// that's empty, the environment variable named by <see cref="ProviderHeader.ValueEnvVar"/>. Headers with
    /// an empty name, or whose value can't be resolved (e.g. a missing env var), are skipped. Order is
    /// preserved. Shared by the forwarding path and the discovery endpoint so both send identical headers.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ResolveExtraHeaders(ProviderOptions provider, IEnvironmentVariableProvider environment)
    {
        if (provider.Headers.Count == 0)
        {
            return [];
        }

        var resolved = new List<KeyValuePair<string, string>>(provider.Headers.Count);
        foreach (var header in provider.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                continue;
            }

            var value = string.IsNullOrWhiteSpace(header.Value)
                ? (string.IsNullOrWhiteSpace(header.ValueEnvVar) ? null : environment.GetVariable(header.ValueEnvVar))
                : header.Value;

            if (value is not null)
            {
                resolved.Add(new KeyValuePair<string, string>(header.Name, value));
            }
        }

        return resolved;
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
    public static IReadOnlyList<string> ApplyToRequest(
        HttpRequestMessage request,
        ProviderOptions provider,
        IEnvironmentVariableProvider environment)
    {
        List<string>? rejected = null;

        foreach (var (headerName, headerValue) in ResolveExtraHeaders(provider, environment))
        {
            if (!request.Headers.TryAddWithoutValidation(headerName, headerValue))
            {
                (rejected ??= []).Add(headerName);
            }
        }

        return rejected ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Resolves an Amazon Bedrock provider's optional explicit AWS credential override
    /// (<see cref="ProviderOptions.AwsAccessKeyIdEnvVar"/>/<see cref="ProviderOptions.AwsSecretAccessKeyEnvVar"/>/
    /// <see cref="ProviderOptions.AwsSessionTokenEnvVar"/>), for <c>IBedrockRuntimeClientFactory</c>.
    /// Returns all-null when access key id or secret key isn't configured/resolvable, signaling "use the
    /// AWS SDK's own default credential chain" rather than a partially-resolved, broken override.
    /// </summary>
    public static (string? AccessKeyId, string? SecretAccessKey, string? SessionToken) ResolveAwsCredentials(ProviderOptions provider, IEnvironmentVariableProvider environment)
    {
        var accessKeyId = ResolveEnvVar(provider.AwsAccessKeyIdEnvVar, environment);
        var secretAccessKey = ResolveEnvVar(provider.AwsSecretAccessKeyEnvVar, environment);

        // Both halves are required; a blank one - the env var unset, or set to an empty/whitespace value -
        // means "not resolvable" and must fall back to the SDK's default credential chain rather than
        // produce a half-configured override that only fails at call time.
        if (accessKeyId is null || secretAccessKey is null)
        {
            return (null, null, null);
        }

        var sessionToken = ResolveEnvVar(provider.AwsSessionTokenEnvVar, environment);
        return (accessKeyId, secretAccessKey, sessionToken);

        static string? ResolveEnvVar(string? envVarName, IEnvironmentVariableProvider environment)
        {
            if (string.IsNullOrWhiteSpace(envVarName))
            {
                return null;
            }

            var value = environment.GetVariable(envVarName);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}

