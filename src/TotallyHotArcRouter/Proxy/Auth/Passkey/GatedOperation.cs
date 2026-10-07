namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Canonical operation names and parameter-binding helpers for one-operation passkey authorizations
/// (ADR-0020). Each gated RPC pairs an operation constant with a deterministic parameters string so an
/// approval for one action cannot be replayed against another.
/// </summary>
public static class GatedOperation
{
    /// <summary>Operation id for copying the management token through the admin API.</summary>
    public const string GetManagementToken = "get_management_token";

    /// <summary>Operation id for rotating the management token through the admin API.</summary>
    public const string RegenerateManagementToken = "regenerate_management_token";

    /// <summary>Operation id for unlocking conversation content (issues a content grant, not a one-op token).</summary>
    public const string ContentUnlock = "content_unlock";

    /// <summary>Operation id for exporting router state (#165).</summary>
    public const string Export = "export";

    /// <summary>Operation id for importing router state (#165).</summary>
    public const string Import = "import";

    /// <summary>
    /// Builds the parameters string for <see cref="GetManagementToken"/> — empty because the operation
    /// has no request-specific binding.
    /// </summary>
    public static string GetManagementTokenParameters() => string.Empty;

    /// <summary>
    /// Builds the parameters string for <see cref="RegenerateManagementToken"/> — empty because rotation
    /// has no request-specific binding beyond the operation name itself.
    /// </summary>
    public static string RegenerateManagementTokenParameters() => string.Empty;

    /// <summary>
    /// Builds the parameters string for <see cref="ContentUnlock"/> — empty; the resulting grant is
    /// issued separately via <see cref="ContentGrantTable"/>.
    /// </summary>
    public static string ContentUnlockParameters() => string.Empty;

    /// <summary>
    /// Builds the parameters string for <see cref="Export"/> — empty until #165 adds archive-specific
    /// binding.
    /// </summary>
    public static string ExportParameters() => string.Empty;

    /// <summary>
    /// Builds the parameters string for an import bound to a staged archive digest (#165).
    /// </summary>
    /// <param name="stagedArchiveSha256Hex">Lowercase hex SHA-256 of the staged import bytes.</param>
    public static string ImportParameters(string stagedArchiveSha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedArchiveSha256Hex);
        return stagedArchiveSha256Hex;
    }

    /// <summary>
    /// Combines <paramref name="operation"/> and <paramref name="parameters"/> into the single binding key
    /// stored with a one-operation authorization.
    /// </summary>
    public static string FormatBinding(string operation, string parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        parameters ??= string.Empty;
        return $"{operation}\0{parameters}";
    }
}
