namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The router's passkey content-gate state as seen by the calling dashboard session (ADR-0020).
/// </summary>
/// <param name="StoreProtected">Whether the router's secret-store ACL probe passed; when not, enrollment and gated operations are refused.</param>
/// <param name="Enrolled">Whether at least one passkey is enrolled. Until then conversation content stays closed.</param>
/// <param name="GrantActive">Whether the call carried a valid <c>x-content-grant</c> header.</param>
/// <param name="GrantExpiresAtUtc">The grant's expiry when the router could attribute one, otherwise <see langword="null"/>.</param>
/// <param name="EnrollmentCommandHint">The elevated-shell flag that mints an enrollment code, as reported by the router.</param>
public sealed record PasskeyGateStatusInfo(
    bool StoreProtected,
    bool Enrolled,
    bool GrantActive,
    DateTimeOffset? GrantExpiresAtUtc,
    string EnrollmentCommandHint);

/// <summary>
/// One enrolled passkey. <paramref name="BackupEligible"/> and <paramref name="BackupState"/> are the
/// WebAuthn BE and BS flags the Settings list renders as sync badges: BE says the credential can sync across
/// devices, BS says it currently is synced.
/// </summary>
/// <param name="Id">The credential id, base64url.</param>
/// <param name="Name">The operator-chosen display name.</param>
/// <param name="CreatedAtUtc">When the credential was enrolled.</param>
/// <param name="BackupEligible">The WebAuthn BE flag (the credential can be synced).</param>
/// <param name="BackupState">The WebAuthn BS flag (the credential is currently synced).</param>
public sealed record PasskeyInfo(
    string Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    bool BackupEligible,
    bool BackupState);

/// <summary>A freshly issued content grant: the bearer token for <c>x-content-grant</c> and when it lapses.</summary>
/// <param name="GrantToken">The opaque bearer token. Held in memory only.</param>
/// <param name="ExpiresAtUtc">When the router stops honoring the token.</param>
public sealed record ContentGrantInfo(string GrantToken, DateTimeOffset ExpiresAtUtc);

/// <summary>A freshly issued single-use authorization for exactly one gated operation.</summary>
/// <param name="AuthorizationToken">The opaque single-use token to pass in the gated request.</param>
/// <param name="ExpiresAtUtc">When the router stops honoring the token.</param>
public sealed record OneOperationAuthorizationInfo(string AuthorizationToken, DateTimeOffset ExpiresAtUtc);

/// <summary>One audit entry from the router's recent passkey approvals list.</summary>
/// <param name="Operation">The gated operation (or <c>enroll</c> / <c>lock</c>) the entry records.</param>
/// <param name="CredentialName">The passkey used, or empty when none was identified.</param>
/// <param name="AtUtc">When it happened.</param>
/// <param name="Outcome">The outcome label, for example <c>succeeded</c> or <c>failed</c>.</param>
public sealed record PasskeyApprovalInfo(string Operation, string CredentialName, DateTimeOffset AtUtc, string Outcome);

/// <summary>
/// An export a command-line caller has asked the operator to approve (ADR-0020, Amendment 1). What the
/// approval view shows is exactly what the router binds the passkey ceremony to.
/// </summary>
/// <param name="ApprovalId">The router's opaque id for the request.</param>
/// <param name="Filter">Which turns the export would include.</param>
/// <param name="DestinationPath">Where on the router machine the zip would be written.</param>
/// <param name="CreatedAtUtc">When the request was filed.</param>
/// <param name="ExpiresAtUtc">When the request lapses if nobody decides it.</param>
public sealed record PendingApprovalInfo(
    string ApprovalId,
    ConversationExportFilterInfo Filter,
    string DestinationPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Every <c>PasskeyAdminService</c> RPC the dashboard needs for ADR-0020's content gate: gate status,
/// passkey enrollment, content unlock and lock, one-operation authorizations, and the approvals audit list.
/// An interface so <c>PasskeyAdminStore</c> can be unit-tested against a fake without a live proxy,
/// mirroring <see cref="IManagementTokenAdminClient"/>. The WebAuthn <c>options</c> and <c>response</c>
/// documents are passed through as opaque JSON between the router and the browser's
/// <c>navigator.credentials</c> API.
/// </summary>
public interface IPasskeyAdminClient
{
    /// <summary>Reads the gate status as seen by this session, including whether its grant is active.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<PasskeyGateStatusInfo> GetGateStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists every enrolled passkey.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<IReadOnlyList<PasskeyInfo>> ListPasskeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends a single-use enrollment code to start a registration ceremony.
    /// </summary>
    /// <param name="enrollmentCode">The code minted by the elevated enrollment command.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The WebAuthn <c>PublicKeyCredentialCreationOptions</c> JSON for <c>navigator.credentials.create</c>.</returns>
    /// <exception cref="GrpcAdminException">The code was rejected, or the call failed or the router is unreachable.</exception>
    Task<string> BeginEnrollmentAsync(string enrollmentCode, CancellationToken cancellationToken = default);

    /// <summary>Completes a registration ceremony with the browser's attestation response.</summary>
    /// <param name="attestationJson">The attestation response JSON produced by the browser.</param>
    /// <param name="name">The operator-chosen credential name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The enrolled credential.</returns>
    /// <exception cref="GrpcAdminException">Verification failed, or the call failed or the router is unreachable.</exception>
    Task<PasskeyInfo> FinishEnrollmentAsync(string attestationJson, string name,
        CancellationToken cancellationToken = default);

    /// <summary>Starts a content-unlock assertion ceremony.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The WebAuthn <c>PublicKeyCredentialRequestOptions</c> JSON for <c>navigator.credentials.get</c>.</returns>
    /// <exception cref="GrpcAdminException">No passkey is enrolled, or the call failed or the router is unreachable.</exception>
    Task<string> BeginContentUnlockAsync(CancellationToken cancellationToken = default);

    /// <summary>Completes a content-unlock ceremony, exchanging the assertion for a content grant.</summary>
    /// <param name="assertionJson">The assertion response JSON produced by the browser.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">Verification failed, or the call failed or the router is unreachable.</exception>
    Task<ContentGrantInfo> FinishContentUnlockAsync(string assertionJson,
        CancellationToken cancellationToken = default);

    /// <summary>Starts an assertion ceremony bound to one gated operation.</summary>
    /// <param name="operation">A <see cref="PasskeyOperations"/> constant.</param>
    /// <param name="parameters">The operation's bound parameters; <see cref="PasskeyOperations.NoParameters"/> for the token operations.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The WebAuthn <c>PublicKeyCredentialRequestOptions</c> JSON for <c>navigator.credentials.get</c>.</returns>
    /// <exception cref="GrpcAdminException">The operation is unsupported, no passkey is enrolled, or the call failed.</exception>
    Task<string> BeginOneOperationAsync(string operation, string parameters,
        CancellationToken cancellationToken = default);

    /// <summary>Completes an operation-bound ceremony, exchanging the assertion for a single-use authorization.</summary>
    /// <param name="assertionJson">The assertion response JSON produced by the browser.</param>
    /// <param name="operation">The same operation passed to <see cref="BeginOneOperationAsync"/>.</param>
    /// <param name="parameters">The same parameters passed to <see cref="BeginOneOperationAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">Verification failed, or the call failed or the router is unreachable.</exception>
    Task<OneOperationAuthorizationInfo> FinishOneOperationAsync(string assertionJson, string operation,
        string parameters, CancellationToken cancellationToken = default);

    /// <summary>Revokes every content grant on the router, locking conversation content again.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task LockContentAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the router's most recent passkey approvals and refusals, newest first as the router orders them.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<IReadOnlyList<PasskeyApprovalInfo>> ListRecentApprovalsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the approval requests command-line callers have filed and nobody has decided, oldest first.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The call failed or the router is unreachable.</exception>
    Task<IReadOnlyList<PendingApprovalInfo>> ListPendingApprovalsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the passkey ceremony for one pending request. The router binds the challenge to the request's own
    /// operation and digest, so the ceremony can approve nothing else.
    /// </summary>
    /// <param name="approvalId">The request's id from <see cref="ListPendingApprovalsAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The WebAuthn <c>PublicKeyCredentialRequestOptions</c> JSON for <c>navigator.credentials.get</c>.</returns>
    /// <exception cref="GrpcAdminException">The request is no longer waiting, no passkey is enrolled, or the call failed.</exception>
    Task<string> BeginApprovalAsync(string approvalId, CancellationToken cancellationToken = default);

    /// <summary>Completes the ceremony begun by <see cref="BeginApprovalAsync"/> and approves the request.</summary>
    /// <param name="approvalId">The same request id passed to <see cref="BeginApprovalAsync"/>.</param>
    /// <param name="assertionJson">The assertion response JSON produced by the browser.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">Verification failed, the request is no longer waiting, or the call failed.</exception>
    Task FinishApprovalAsync(string approvalId, string assertionJson, CancellationToken cancellationToken = default);

    /// <summary>Refuses a pending request; the command waiting on it stops with a denial.</summary>
    /// <param name="approvalId">The request's id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The request is no longer waiting, or the call failed.</exception>
    Task DenyApprovalAsync(string approvalId, CancellationToken cancellationToken = default);
}
