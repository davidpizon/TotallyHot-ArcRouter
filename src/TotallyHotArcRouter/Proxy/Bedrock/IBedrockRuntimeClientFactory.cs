using Amazon;
using Amazon.BedrockRuntime;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TotallyHot.ArcRouter.Proxy.Bedrock;

/// <summary>
/// Builds an <see cref="IAmazonBedrockRuntime"/> client for a resolved Bedrock route. A seam purely for
/// testability - the real implementation constructs a genuine AWS SDK client (which would make live
/// AWS calls), so tests substitute a fake <see cref="IAmazonBedrockRuntime"/> instead of hitting AWS.
/// </summary>
public interface IBedrockRuntimeClientFactory
{
    /// <summary>Builds a client scoped to <paramref name="route"/>'s region and (optional) explicit credentials.</summary>
    /// <param name="route">The resolved Bedrock route; <see cref="ResolvedModelRoute.AwsRegion"/> must be set.</param>
    IAmazonBedrockRuntime Create(ResolvedModelRoute route);
}

/// <inheritdoc cref="IBedrockRuntimeClientFactory" />
/// <remarks>
/// AWS SDK clients are thread-safe and meant to be long-lived: each one owns an HttpClient and its
/// connection pool, so building (and disposing) one per request churns connections and risks socket
/// exhaustion under load. This factory is a DI singleton, so it caches one client per distinct
/// (region + credential identity) and hands the same instance back for matching routes; callers must
/// NOT dispose what they receive. The cached clients are disposed when the singleton itself is (app
/// shutdown). A credential rotation produces a new cache entry rather than mutating a live client.
/// </remarks>
public sealed class BedrockRuntimeClientFactory : IBedrockRuntimeClientFactory, IDisposable
{
    // Lazy value so a race on GetOrAdd constructs exactly one client per key (the loser's factory
    // delegate may run, but only the winning Lazy's .Value is ever materialized).
    private readonly ConcurrentDictionary<string, Lazy<IAmazonBedrockRuntime>> _clients = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <inheritdoc />
    public IAmazonBedrockRuntime Create(ResolvedModelRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (string.IsNullOrWhiteSpace(route.AwsRegion))
        {
            throw new InvalidOperationException(
                $"Bedrock provider '{route.Provider}' has no AwsRegion configured; set ModelRouting:Providers:{route.Provider}:AwsRegion.");
        }

        // Key by everything that distinguishes one built client from another: region plus a non-reversible
        // fingerprint of the explicit credential triple (or a marker for "use the default chain"). Two routes
        // that resolve to the same region + credentials share a client; a rotated secret keys to a fresh entry.
        // Fingerprinting (rather than embedding the raw values) keeps this long-lived singleton from holding an
        // extra plaintext copy of the secret access key / session token in its cache keys.
        var key = route.AwsAccessKeyId is not null && route.AwsSecretAccessKey is not null
            ? $"{route.AwsRegion}\nexplicit:{CredentialFingerprint(route.AwsAccessKeyId, route.AwsSecretAccessKey, route.AwsSessionToken)}"
            : $"{route.AwsRegion}\n<default-chain>";

        return _clients.GetOrAdd(key, _ => new Lazy<IAmazonBedrockRuntime>(() => BuildClient(route))).Value;
    }

    // A non-reversible fingerprint of the credential material, used only to distinguish cache entries - never
    // the raw secret, so the secret access key / session token are not duplicated as long-lived plaintext
    // dictionary keys (heap dumps, diagnostics). The access key id is not itself a secret but is folded in so
    // two distinct credential sets never collide onto the same client.
    /// <summary>
    /// Computes a non-reversible SHA-256 fingerprint of the explicit credential triple, used solely as a
    /// cache key so distinct credential sets never collide onto the same cached client.
    /// </summary>
    private static string CredentialFingerprint(string accessKeyId, string secretAccessKey, string? sessionToken)
    {
        // Hashed incrementally, one field at a time, rather than building a single interpolated string
        // first: that would create an extra long-lived managed string holding the full secret access key
        // (and session token) in memory before it's ever hashed, widening the window a heap dump or
        // diagnostic snapshot could catch it in. Each field is appended as its own transient UTF-8 byte
        // buffer instead, and a separator byte breaks the field boundary so e.g. ("ab","c") and ("a","bc")
        // can never hash identically.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, accessKeyId);
        AppendField(hash, secretAccessKey);
        AppendField(hash, sessionToken);
        return Convert.ToHexString(hash.GetHashAndReset());

        static void AppendField(IncrementalHash hash, string? value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value ?? string.Empty));
            hash.AppendData([0]);
        }
    }

    /// <summary>
    /// Constructs a new Bedrock runtime client for the given route's region, using explicit credentials
    /// when configured or falling back to the AWS SDK's default credential chain otherwise.
    /// </summary>
    private static IAmazonBedrockRuntime BuildClient(ResolvedModelRoute route)
    {
        var region = RegionEndpoint.GetBySystemName(route.AwsRegion);

        // An explicit access key id + secret key configured for this provider (ProviderOptions'
        // AwsAccessKeyIdEnvVar/AwsSecretAccessKeyEnvVar) takes precedence; otherwise the SDK's own
        // default AWS credential chain applies (environment variables, ~/.aws/credentials profiles,
        // instance/container role, etc.) - answering the design doc's open "env vars? ~/.aws/credentials?
        // both?" question for free, since the SDK's default chain already covers both without this
        // codebase needing to implement either itself.
        if (route.AwsAccessKeyId is not null && route.AwsSecretAccessKey is not null)
        {
            return route.AwsSessionToken is not null
                ? new AmazonBedrockRuntimeClient(route.AwsAccessKeyId, route.AwsSecretAccessKey, route.AwsSessionToken, region)
                : new AmazonBedrockRuntimeClient(route.AwsAccessKeyId, route.AwsSecretAccessKey, region);
        }

        return new AmazonBedrockRuntimeClient(region);
    }

    /// <summary>Disposes every cached client. Called by the DI container when the singleton is torn down.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var client in _clients.Values)
        {
            if (client.IsValueCreated)
            {
                client.Value.Dispose();
            }
        }

        _clients.Clear();
    }
}

