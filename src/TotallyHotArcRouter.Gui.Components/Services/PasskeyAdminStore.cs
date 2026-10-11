using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>
/// Singleton view-model for ADR-0020's passkey content gate: the gate status, the enrolled passkeys, the
/// recent-approvals audit list, and every ceremony the dashboard runs - enrolling a passkey, unlocking
/// conversation content, locking it again, and earning a single-use authorization for one gated operation.
/// Wraps <see cref="IPasskeyAdminClient"/> in the shared <see cref="AdminStoreBase{TClient}"/> shape, so
/// the Settings section and the Sessions tab survive modal close/reopen and degrade gracefully when the
/// router is not running.
/// </summary>
/// <remarks>
/// <para>
/// A ceremony is always three steps - ask the router for options, let the browser's authenticator answer
/// them (<see cref="IWebAuthnCeremony"/>), hand the answer back - and the router consumes its challenge
/// before verifying, so a failed or abandoned ceremony cannot be replayed. This store is the only place the
/// three steps are sequenced, so a new gated operation is one call to <see cref="AuthorizeOperationAsync"/>.
/// </para>
/// <para>
/// Load/mutation split, as in <see cref="AdminStoreBase{TClient}"/>: <see cref="RefreshAsync"/> swallows a
/// failure into the reachability state, while every ceremony records it and rethrows - the operator has to be
/// told that the unlock, enrollment, or approval they just attempted did not happen. A ceremony the
/// operator abandoned in the browser surfaces as <see cref="WebAuthnCeremonyException"/>, which is not a
/// router failure and does not touch reachability.
/// </para>
/// </remarks>
public sealed class PasskeyAdminStore : AdminStoreBase<IPasskeyAdminClient>
{
    private readonly IWebAuthnCeremony _ceremony;
    private readonly ContentGrantStore _contentGrant;

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyAdminStore"/> class over the shared
    /// <see cref="IRouterChannelProvider"/> - see its remarks.
    /// </summary>
    /// <param name="channelProvider">Supplies the shared call invoker this store's client is constructed over.</param>
    /// <param name="ceremony">Runs the WebAuthn ceremonies in the host's authenticator API.</param>
    /// <param name="contentGrant">Receives the grant a successful unlock earns, and is cleared by a lock.</param>
    /// <param name="logger">Optional logger.</param>
    public PasskeyAdminStore(
        IRouterChannelProvider channelProvider,
        IWebAuthnCeremony ceremony,
        ContentGrantStore contentGrant,
        ILogger<PasskeyAdminStore>? logger = null)
        : base(client: new PasskeyAdminClient(channelProvider.CallInvoker), logger: logger)
    {
        ArgumentNullException.ThrowIfNull(ceremony);
        ArgumentNullException.ThrowIfNull(contentGrant);
        _ceremony = ceremony;
        _contentGrant = contentGrant;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyAdminStore"/> class over a caller-supplied client.
    /// The seam tests use to drive the store without a live proxy; the caller owns the client's lifetime.
    /// </summary>
    /// <param name="client">The passkey admin client to drive.</param>
    /// <param name="ceremony">Runs the WebAuthn ceremonies in the host's authenticator API.</param>
    /// <param name="contentGrant">Receives the grant a successful unlock earns, and is cleared by a lock.</param>
    /// <param name="logger">Optional logger.</param>
    public PasskeyAdminStore(
        IPasskeyAdminClient client,
        IWebAuthnCeremony ceremony,
        ContentGrantStore contentGrant,
        ILogger<PasskeyAdminStore>? logger = null)
        : base(client: client, logger: logger)
    {
        ArgumentNullException.ThrowIfNull(ceremony);
        ArgumentNullException.ThrowIfNull(contentGrant);
        _ceremony = ceremony;
        _contentGrant = contentGrant;
    }

    /// <summary>The gate status as of the last successful refresh, or <see langword="null"/> before one.</summary>
    public PasskeyGateStatusInfo? GateStatus { get; private set; }

    /// <summary>The enrolled passkeys as of the last successful refresh. Empty before one, and until enrollment.</summary>
    public IReadOnlyList<PasskeyInfo> Passkeys { get; private set; } = [];

    /// <summary>The router's recent approvals and refusals as of the last successful refresh.</summary>
    public IReadOnlyList<PasskeyApprovalInfo> Approvals { get; private set; } = [];

    /// <summary>
    /// Gets whether the router reported at least one enrolled passkey. <see langword="false"/> before the
    /// first refresh, which is the safe reading: the UI then shows the enrollment hint rather than an unlock
    /// button that cannot work.
    /// </summary>
    public bool IsEnrolled => GateStatus is { Enrolled: true };

    /// <summary>
    /// Gets the elevated-shell command that mints an enrollment code, as the router reported it, or the
    /// built-in flag name before the first refresh.
    /// </summary>
    public string EnrollmentCommandHint =>
        GateStatus is { EnrollmentCommandHint: { Length: > 0 } hint } ? hint : PasskeyOperations.EnrollmentCommandFlag;

    /// <summary>
    /// Reads the gate status, the passkey list, and the recent approvals. Failures are swallowed and surfaced
    /// via <see cref="AdminStoreBase{TClient}.IsReachable"/>/<see cref="AdminStoreBase{TClient}.LastError"/>.
    /// Also reconciles the local grant with the router's view: if this dashboard holds a grant but the router
    /// no longer recognises it (a router restart, or a Lock from another tab), the local copy and every
    /// cached conversation text field are dropped, so the UI never shows text the router would no longer serve.
    /// </summary>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct =>
            {
                var grantAtStart = _contentGrant.Token;
                var status = await Client.GetGateStatusAsync(ct).ConfigureAwait(false);
                var passkeys = await Client.ListPasskeysAsync(ct).ConfigureAwait(false);
                var approvals = await Client.ListRecentApprovalsAsync(ct).ConfigureAwait(false);

                GateStatus = status;
                Passkeys = passkeys;
                Approvals = approvals;

                // Only when the request that produced this status actually carried the grant now held: a
                // grant minted while the request was in flight was not on it and says nothing about it.
                if (grantAtStart is not null && !status.GrantActive &&
                    string.Equals(a: _contentGrant.Token, b: grantAtStart, comparisonType: StringComparison.Ordinal))
                    _contentGrant.Clear();
            },
            "read the passkey gate",
            cancellationToken);
    }

    /// <summary>
    /// Enrolls a new passkey: spends <paramref name="enrollmentCode"/> to start a registration ceremony, has
    /// the browser create the credential, and hands the attestation back to the router. Refreshes the list on
    /// success.
    /// </summary>
    /// <param name="enrollmentCode">The single-use code minted by the elevated enrollment command.</param>
    /// <param name="name">The operator-chosen display name; a blank name is replaced by <c>Passkey</c> on the router.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The enrolled credential.</returns>
    /// <exception cref="GrpcAdminException">The router rejected the code or the attestation, or is unreachable.</exception>
    /// <exception cref="WebAuthnCeremonyException">The browser ceremony was dismissed or failed.</exception>
    public async Task<PasskeyInfo> EnrollAsync(string enrollmentCode, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentCode);

        PasskeyInfo enrolled;
        try
        {
            var options = await Client.BeginEnrollmentAsync(enrollmentCode.Trim(), cancellationToken)
                .ConfigureAwait(false);
            var attestation = await _ceremony.CreateCredentialAsync(options, cancellationToken)
                .ConfigureAwait(false);
            enrolled = await Client.FinishEnrollmentAsync(attestation, name, cancellationToken)
                .ConfigureAwait(false);
            RecordSuccess();
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a passkey enrollment");
            throw;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return enrolled;
    }

    /// <summary>
    /// Unlocks conversation content: runs the content-unlock ceremony (user verification required) and stores
    /// the resulting grant in <see cref="ContentGrantStore"/>, which starts the live stream over with the new
    /// header and reloads persisted sessions.
    /// </summary>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <exception cref="GrpcAdminException">No passkey is enrolled, the router rejected the assertion, or is unreachable.</exception>
    /// <exception cref="WebAuthnCeremonyException">The browser ceremony was dismissed or failed.</exception>
    public async Task UnlockContentAsync(CancellationToken cancellationToken = default)
    {
        ContentGrantInfo grant;
        try
        {
            var options = await Client.BeginContentUnlockAsync(cancellationToken).ConfigureAwait(false);
            var assertion = await _ceremony.GetAssertionAsync(options, cancellationToken).ConfigureAwait(false);
            grant = await Client.FinishContentUnlockAsync(assertion, cancellationToken).ConfigureAwait(false);
            RecordSuccess();
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a content unlock");
            throw;
        }

        GateStatus = (GateStatus ?? new PasskeyGateStatusInfo(StoreProtected: true, Enrolled: true,
            GrantActive: true, GrantExpiresAtUtc: null,
            EnrollmentCommandHint: PasskeyOperations.EnrollmentCommandFlag)) with
        {
            Enrolled = true,
            GrantActive = true,
            GrantExpiresAtUtc = grant.ExpiresAtUtc
        };
        _contentGrant.SetGrant(token: grant.GrantToken, expiresAtUtc: grant.ExpiresAtUtc);
        NotifyChanged();
    }

    /// <summary>
    /// Locks conversation content: drops the local grant and every cached conversation text field first, then
    /// asks the router to revoke every grant. The local clear comes first and is unconditional, so a router
    /// that cannot be reached still leaves the dashboard locked - failing closed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the revoke call (the local clear has already happened).</param>
    /// <exception cref="GrpcAdminException">
    /// The router could not be told to revoke. The dashboard is locked regardless; the router-side grant
    /// lapses on its own expiry.
    /// </exception>
    public async Task LockAsync(CancellationToken cancellationToken = default)
    {
        _contentGrant.Clear();
        if (GateStatus is { } status)
            GateStatus = status with { GrantActive = false, GrantExpiresAtUtc = null };

        try
        {
            await Client.LockContentAsync(cancellationToken).ConfigureAwait(false);
            RecordSuccess(marksLoaded: false);
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a content lock");
            throw;
        }
        finally
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Earns a single-use authorization for one gated operation: begins a ceremony bound to
    /// <paramref name="operation"/> and <paramref name="parameters"/>, has the browser answer it with user
    /// verification, and returns the router's authorization token. The token is valid for one call of that
    /// operation, briefly, so the caller spends it immediately and never stores it.
    /// </summary>
    /// <param name="operation">A <see cref="PasskeyOperations"/> constant.</param>
    /// <param name="parameters">The operation's bound parameters; <see cref="PasskeyOperations.NoParameters"/> for the token operations.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The authorization token to pass in the gated request.</returns>
    /// <exception cref="GrpcAdminException">No passkey is enrolled, the router rejected the assertion, or is unreachable.</exception>
    /// <exception cref="WebAuthnCeremonyException">The browser ceremony was dismissed or failed.</exception>
    public async Task<string> AuthorizeOperationAsync(string operation, string parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentNullException.ThrowIfNull(parameters);

        try
        {
            var options = await Client.BeginOneOperationAsync(operation, parameters, cancellationToken)
                .ConfigureAwait(false);
            var assertion = await _ceremony.GetAssertionAsync(options, cancellationToken).ConfigureAwait(false);
            var authorization = await Client
                .FinishOneOperationAsync(assertion, operation, parameters, cancellationToken)
                .ConfigureAwait(false);
            RecordSuccess(marksLoaded: false);
            return authorization.AuthorizationToken;
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a passkey operation approval");
            throw;
        }
    }

    /// <summary>
    /// Gets the approval requests command-line callers have filed and nobody has decided, as of the last
    /// <see cref="RefreshPendingApprovalsAsync"/>. Empty before one. The list is a snapshot: a request can lapse or
    /// be decided from another tab after it was read, which the router then reports on approve or deny.
    /// </summary>
    public IReadOnlyList<PendingApprovalInfo> PendingApprovals { get; private set; } = [];

    /// <summary>
    /// Reads the pending approval requests (ADR-0020, Amendment 1). A failure is swallowed into the reachability
    /// state, like <see cref="RefreshAsync"/>, and leaves the previous list in place.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task RefreshPendingApprovalsAsync(CancellationToken cancellationToken = default)
    {
        return LoadGuardedAsync(
            async ct => PendingApprovals = await Client.ListPendingApprovalsAsync(ct).ConfigureAwait(false),
            "read the pending approval requests",
            cancellationToken,
            marksLoaded: false);
    }

    /// <summary>
    /// Approves one pending request with a passkey: the router begins a ceremony bound to the request's own
    /// operation and digest, the browser answers it with user verification, and the router marks the request
    /// approved so the waiting command can collect its authorization. The dashboard never receives that
    /// authorization. Refreshes the pending list afterwards.
    /// </summary>
    /// <param name="approvalId">The request's id from <see cref="PendingApprovals"/>.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <exception cref="GrpcAdminException">The request is no longer waiting, the router rejected the assertion, or it is unreachable.</exception>
    /// <exception cref="WebAuthnCeremonyException">The browser ceremony was dismissed or failed.</exception>
    public async Task ApprovePendingAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);

        try
        {
            var options = await Client.BeginApprovalAsync(approvalId, cancellationToken).ConfigureAwait(false);
            var assertion = await _ceremony.GetAssertionAsync(options, cancellationToken).ConfigureAwait(false);
            await Client.FinishApprovalAsync(approvalId, assertion, cancellationToken).ConfigureAwait(false);
            RecordSuccess(marksLoaded: false);
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "an approval of a pending request");
            throw;
        }

        await RefreshPendingApprovalsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses one pending request; the command waiting on it stops with a denial and writes nothing. No passkey
    /// is needed to say no. Refreshes the pending list afterwards.
    /// </summary>
    /// <param name="approvalId">The request's id from <see cref="PendingApprovals"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="GrpcAdminException">The request is no longer waiting, or the router is unreachable.</exception>
    public async Task DenyPendingAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);

        try
        {
            await Client.DenyApprovalAsync(approvalId, cancellationToken).ConfigureAwait(false);
            RecordSuccess(marksLoaded: false);
        }
        catch (GrpcAdminException ex)
        {
            RecordFailure(exception: ex, description: "a denial of a pending request");
            throw;
        }

        await RefreshPendingApprovalsAsync(cancellationToken).ConfigureAwait(false);
    }
}
