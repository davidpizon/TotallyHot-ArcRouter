namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// The secret read/write collaborator split out of <see cref="ManagementFacade"/> per
/// <see href="../../../../docs/adr/0006-split-managementfacade-along-crud-aggregate-boundaries.md"/>:
/// storing and clearing a provider's reconciliation Admin API key in the protected secret store
/// (<c>docs/router/secrets-at-rest-plan.md</c> §7). Reachable only through <see cref="ManagementFacade"/>'s
/// public methods - it is constructed directly by the facade and is not registered in DI as an
/// independently reachable service, so <see cref="ManagementFacade"/>'s public method set remains the
/// single security boundary the ADR describes.
/// </summary>
internal sealed class SecretManagementService
{
    // Only these two are recognized by BuildCostReconcilers (docs/router/agent-cost-tracking.md §3.5), so
    // this is the complete set of names SetSecret/DeleteSecret may ever touch.
    private static readonly string[] RecognizedReconciliationProviders = ["openai", "anthropic"];

    private readonly ISecretWriter? _secretWriter;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretManagementService"/> class.
    /// </summary>
    /// <param name="dependencies">The same optional collaborators bag <see cref="ManagementFacade"/> was constructed with; only <see cref="ManagementFacadeDependencies.SecretWriter"/> is used here.</param>
    public SecretManagementService(ManagementFacadeDependencies? dependencies)
    {
        _secretWriter = dependencies?.SecretWriter;
    }

    /// <summary>
    /// Stores <paramref name="value"/> as a provider's reconciliation Admin API key
    /// (docs/router/secrets-at-rest-plan.md §7), taking effect on the next reconciliation cycle with no
    /// restart required. The public route is named by secret (<c>PUT /admin/secrets/{name}</c>) rather than
    /// by provider so it matches the plan's write-only-secrets shape, but only the fixed
    /// <c>reconciliation:{openai|anthropic}:admin-key</c> names are accepted - this is not a generic secret
    /// store write endpoint.
    /// </summary>
    public ManagementResult<object?> SetSecret(string name, string value)
    {
        if (_secretWriter is null)
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.Unavailable, "The protected secret store is unavailable on this platform.");
        }

        if (!TryParseAdminKeySecretName(name, out var provider))
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.InvalidRequest, $"Unsupported secret name '{name}'.");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.InvalidRequest, "value must not be blank.");
        }

        try
        {
            _secretWriter.Write(AdminKeySecretName(provider), value);
            return ManagementResult<object?>.Ok(null);
        }
        catch (PlatformNotSupportedException)
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.Unavailable, "The protected secret store is unavailable on this platform.");
        }
    }

    /// <summary>Clears a stored secret by name - the only counterpart to <see cref="SetSecret"/>, same name restriction. There is deliberately no read counterpart (docs/router/secrets-at-rest-plan.md §4).</summary>
    public ManagementResult<object?> DeleteSecret(string name)
    {
        if (_secretWriter is null)
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.Unavailable, "The protected secret store is unavailable on this platform.");
        }

        if (!TryParseAdminKeySecretName(name, out var provider))
        {
            return ManagementResult<object?>.Fail(ManagementErrorType.InvalidRequest, $"Unsupported secret name '{name}'.");
        }

        _secretWriter.Delete(AdminKeySecretName(provider));
        return ManagementResult<object?>.Ok(null);
    }

    /// <summary>Parses a secret name as <c>reconciliation:{provider}:admin-key</c> for a recognized provider, the only shape <see cref="SetSecret"/>/<see cref="DeleteSecret"/> accept.</summary>
    private static bool TryParseAdminKeySecretName(string name, out string provider)
    {
        provider = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var parts = name.Split(':');
        if (parts.Length != 3 || parts[0] != "reconciliation" || parts[2] != "admin-key")
        {
            return false;
        }

        foreach (var candidate in RecognizedReconciliationProviders)
        {
            if (string.Equals(candidate, parts[1], StringComparison.OrdinalIgnoreCase))
            {
                provider = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The protected-store name for a provider's reconciliation Admin API key (docs/router/secrets-at-rest-plan.md §3's naming convention), matching <c>Hosting.ServiceCollectionExtensions.AdminApiKeySecretName</c>.</summary>
    private static string AdminKeySecretName(string provider) => $"reconciliation:{provider}:admin-key";
}
