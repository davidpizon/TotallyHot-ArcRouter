using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Shared orchestration for ADR-0020's passkey content gate: store ACL posture, enrollment state, content
/// grants on gRPC metadata, and one-operation authorizations. gRPC services call these helpers rather
/// than reimplementing the rules per RPC.
/// </summary>
public sealed class ContentGate
{
    /// <summary>gRPC metadata header carrying a content-grant bearer token.</summary>
    public const string ContentGrantHeaderName = "x-content-grant";

    /// <summary>
    /// Operator-facing detail when no passkey is enrolled yet. Names <see cref="EnrollmentCliCommand.FlagName"/>.
    /// </summary>
    public const string EnrollmentRequiredDetail =
        "Conversation content is locked until a passkey is enrolled. Run the router with " +
        EnrollmentCliCommand.FlagName +
        " from an elevated shell, then add a passkey in the dashboard.";

    private readonly SecretStoreAclProbe _aclProbe;
    private readonly IPasskeyCredentialStore _credentialStore;
    private readonly ContentGrantTable _contentGrants;
    private readonly OneOperationAuthorizationTable _oneOperationAuthorizations;

    /// <summary>Initializes a new instance of the <see cref="ContentGate"/> class.</summary>
    public ContentGate(
        SecretStoreAclProbe aclProbe,
        IPasskeyCredentialStore credentialStore,
        ContentGrantTable contentGrants,
        OneOperationAuthorizationTable oneOperationAuthorizations)
    {
        ArgumentNullException.ThrowIfNull(aclProbe);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(contentGrants);
        ArgumentNullException.ThrowIfNull(oneOperationAuthorizations);
        _aclProbe = aclProbe;
        _credentialStore = credentialStore;
        _contentGrants = contentGrants;
        _oneOperationAuthorizations = oneOperationAuthorizations;
    }

    /// <summary>Gets whether the last <see cref="RefreshStoreProtection"/> saw an acceptable store ACL.</summary>
    public bool StoreProtected { get; private set; }

    /// <summary>Re-runs <see cref="SecretStoreAclProbe"/> against the default or supplied secrets path.</summary>
    /// <param name="secretsPath">Optional override; defaults to <see cref="ProtectedSecretStore.DefaultPath"/>.</param>
    public void RefreshStoreProtection(string? secretsPath = null)
    {
        secretsPath ??= ProtectedSecretStore.DefaultPath();
        StoreProtected = _aclProbe.Check(secretsPath);
    }

    /// <summary>Returns whether at least one passkey credential is enrolled.</summary>
    public bool HasPasskeys() => _credentialStore.List().Count > 0;

    /// <summary>
    /// Ensures enrollment may proceed: the secret store ACL probe must pass. Throws
    /// <see cref="RpcException"/> with <see cref="StatusCode.FailedPrecondition"/> when not.
    /// </summary>
    public void EnsureCanEnroll()
    {
        if (!StoreProtected)
        {
            throw new RpcException(new Status(
                statusCode: StatusCode.FailedPrecondition,
                detail: "Passkey enrollment is refused until the protected secret store ACL is repaired."));
        }
    }

    /// <summary>
    /// Ensures the router is past the closed-until-enrolled state. Throws <see cref="RpcException"/> with
    /// <see cref="StatusCode.FailedPrecondition"/> and <see cref="EnrollmentRequiredDetail"/> when no
    /// passkey exists or the store is unprotected.
    /// </summary>
    public void EnsureEnrolled()
    {
        if (!StoreProtected)
        {
            throw new RpcException(new Status(
                statusCode: StatusCode.FailedPrecondition,
                detail: "Gated operations are unavailable until the protected secret store ACL is repaired."));
        }

        if (!HasPasskeys())
        {
            throw new RpcException(new Status(
                statusCode: StatusCode.FailedPrecondition,
                detail: EnrollmentRequiredDetail));
        }
    }

    /// <summary>
    /// Reads <see cref="ContentGrantHeaderName"/> from <paramref name="context"/> and checks the grant
    /// table.
    /// </summary>
    public bool TryGetContentGrant(ServerCallContext context, out string? grantToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        grantToken = GetHeader(context, ContentGrantHeaderName);
        if (string.IsNullOrWhiteSpace(grantToken)) return false;

        // Same closed-until-enrolled boundary as RequireContentGrant: once the last passkey is revoked or the
        // store becomes unprotected, outstanding grants stop working at once instead of at expiry.
        if (!StoreProtected || !HasPasskeys()) return false;

        return _contentGrants.IsValid(grantToken);
    }

    /// <summary>
    /// Requires a valid content grant on <paramref name="context"/>; throws
    /// <see cref="StatusCode.Unauthenticated"/> when missing or expired.
    /// </summary>
    public void RequireContentGrant(ServerCallContext context)
    {
        EnsureEnrolled();
        if (!TryGetContentGrant(context, out _))
        {
            throw new RpcException(new Status(
                statusCode: StatusCode.Unauthenticated,
                detail: "A valid content grant is required. Unlock with a passkey first."));
        }
    }

    /// <summary>
    /// Consumes a one-operation authorization token from <paramref name="authorizationToken"/> for
    /// <paramref name="operation"/> and <paramref name="parameters"/>.
    /// </summary>
    /// <exception cref="RpcException">When enrollment, store protection, or the authorization fails.</exception>
    public void RequireAndConsumeOneOperation(string? authorizationToken, string operation, string parameters)
    {
        EnsureEnrolled();
        if (!_oneOperationAuthorizations.TryConsume(authorizationToken, operation, parameters))
        {
            throw new RpcException(new Status(
                statusCode: StatusCode.Unauthenticated,
                detail: "Passkey verification is required for this operation."));
        }
    }

    private static string? GetHeader(ServerCallContext context, string name)
    {
        foreach (var entry in context.RequestHeaders)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }

        return null;
    }
}
