using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TotallyHot.ArcRouter.Sessions.Export;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// gRPC surface for ADR-0020's passkey content gate: reports gate status, enrolls passkeys behind an
/// elevated-shell enrollment code, unlocks conversation content with a short-lived grant, and issues
/// single-use authorizations for gated operations such as copying the management token. Every ceremony
/// goes through <see cref="IWebAuthnCeremonyService"/>, so challenges are always consumed before
/// verification, and every approval or refusal is recorded in <see cref="PasskeyApprovalLog"/> for the
/// dashboard's audit list. Mapped by <see cref="ProxyServer"/> alongside
/// <see cref="Management.ManagementTokenAdminGrpcService"/>, only when a management token provider and the
/// passkey gate collaborators are configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Browser hand-off (ADR-0020, Amendment 1).</b> A caller that cannot run a passkey ceremony - the
/// <c>--export-conversations</c> command, on every platform - files a request with
/// <see cref="CreateApprovalRequest"/>, the dashboard lists it (<see cref="ListPendingApprovals"/>) and runs the
/// ceremony for it (<see cref="BeginApproval"/>, <see cref="FinishApproval"/>) or refuses it
/// (<see cref="DenyApproval"/>), and the caller collects the outcome with <see cref="WaitForApproval"/>. The
/// router derives the ceremony's operation and digest from the stored request, never from the dashboard's
/// call, so the browser can approve only what it was shown. The one-operation authorization is minted when the
/// caller collects it, not when the operator approves: it never passes through the browser, and its short
/// lifetime starts when the caller can use it.
/// </para>
/// </remarks>
public sealed class PasskeyAdminGrpcService : Contract.PasskeyAdminService.PasskeyAdminServiceBase
{
    private const string OutcomeSucceeded = "succeeded";
    private const string OutcomeFailed = "failed";
    private const string OutcomeDenied = "denied";

    /// <summary>Operator-facing detail when an approval request is unknown, already decided, or has lapsed.</summary>
    private const string NoLongerWaitingDetail =
        "This approval request is no longer waiting: it was already decided, has lapsed, or does not exist.";

    private readonly ContentGate _contentGate;
    private readonly EnrollmentCodeService _enrollmentCodes;
    private readonly IWebAuthnCeremonyService _ceremonies;
    private readonly ContentGrantTable _contentGrants;
    private readonly OneOperationAuthorizationTable _oneOperationAuthorizations;
    private readonly PasskeyApprovalLog _approvalLog;
    private readonly IPasskeyCredentialStore _credentialStore;
    private readonly PasskeyOptions _options;
    private readonly PendingApprovalTable _pendingApprovals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PasskeyAdminGrpcService> _logger;

    /// <summary>Initializes a new instance of the <see cref="PasskeyAdminGrpcService"/> class.</summary>
    /// <param name="contentGate">Shared gate rules: store ACL posture, enrollment state, grant header checks.</param>
    /// <param name="enrollmentCodes">Validates the single-use code minted through the elevated channel.</param>
    /// <param name="ceremonies">Runs the WebAuthn registration and assertion ceremonies.</param>
    /// <param name="contentGrants">Issues and revokes content grants.</param>
    /// <param name="oneOperationAuthorizations">Issues single-use operation authorizations.</param>
    /// <param name="approvalLog">Receives one entry per approval or refusal.</param>
    /// <param name="credentialStore">Lists enrolled credentials for the status and list RPCs.</param>
    /// <param name="options">Supplies the content-grant lifetime reported in grant responses.</param>
    /// <param name="pendingApprovals">Holds the requests a caller without a browser has filed for the operator to approve.</param>
    /// <param name="timeProvider">Clock for timestamps and expiries; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Records gate decisions. Optional so tests can omit it.</param>
    public PasskeyAdminGrpcService(
        ContentGate contentGate,
        EnrollmentCodeService enrollmentCodes,
        IWebAuthnCeremonyService ceremonies,
        ContentGrantTable contentGrants,
        OneOperationAuthorizationTable oneOperationAuthorizations,
        PasskeyApprovalLog approvalLog,
        IPasskeyCredentialStore credentialStore,
        PasskeyOptions options,
        PendingApprovalTable pendingApprovals,
        TimeProvider? timeProvider = null,
        ILogger<PasskeyAdminGrpcService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pendingApprovals);
        ArgumentNullException.ThrowIfNull(contentGate);
        ArgumentNullException.ThrowIfNull(enrollmentCodes);
        ArgumentNullException.ThrowIfNull(ceremonies);
        ArgumentNullException.ThrowIfNull(contentGrants);
        ArgumentNullException.ThrowIfNull(oneOperationAuthorizations);
        ArgumentNullException.ThrowIfNull(approvalLog);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(options);
        _contentGate = contentGate;
        _enrollmentCodes = enrollmentCodes;
        _ceremonies = ceremonies;
        _contentGrants = contentGrants;
        _oneOperationAuthorizations = oneOperationAuthorizations;
        _approvalLog = approvalLog;
        _credentialStore = credentialStore;
        _options = options;
        _pendingApprovals = pendingApprovals;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PasskeyAdminGrpcService>.Instance;
    }

    /// <inheritdoc/>
    public override Task<Contract.PasskeyGateStatusResponse> GetPasskeyGateStatus(
        Contract.GetPasskeyGateStatusRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new Contract.PasskeyGateStatusResponse
        {
            StoreProtected = _contentGate.StoreProtected,
            Enrolled = _contentGate.HasPasskeys(),
            GrantActive = _contentGate.TryGetContentGrant(context),
            EnrollmentCommandHint = EnrollmentCliCommand.FlagName
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.ListPasskeysResponse> ListPasskeys(
        Contract.ListPasskeysRequest request,
        ServerCallContext context)
    {
        var response = new Contract.ListPasskeysResponse();
        foreach (var credential in _credentialStore.List()) response.Credentials.Add(ToInfo(credential));
        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public override Task<Contract.WebAuthnOptionsResponse> BeginEnrollment(
        Contract.BeginEnrollmentRequest request,
        ServerCallContext context)
    {
        return Guarded(() =>
        {
            _contentGate.EnsureCanEnroll();

            if (string.IsNullOrWhiteSpace(request.EnrollmentCode))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "An enrollment code is required."));

            if (!_enrollmentCodes.TryConsume(request.EnrollmentCode))
            {
                _approvalLog.Record("enroll", string.Empty, _timeProvider.GetUtcNow(), OutcomeFailed);
                _logger.LogWarning("Passkey enrollment refused: the enrollment code was invalid or expired");
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    "The enrollment code is invalid or has expired."));
            }

            // Single use: TryConsume spent the code atomically, so one code cannot enroll twice.
            return new Contract.WebAuthnOptionsResponse { OptionsJson = _ceremonies.BeginRegistration() };
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.PasskeyCredentialResponse> FinishEnrollment(
        Contract.FinishEnrollmentRequest request,
        ServerCallContext context)
    {
        return Guarded(() =>
        {
            _contentGate.EnsureCanEnroll();

            var name = string.IsNullOrWhiteSpace(request.Name) ? "Passkey" : request.Name.Trim();
            var record = RunCeremony(
                () => _ceremonies.FinishRegistration(request.AttestationJson, name),
                operation: "enroll",
                credentialName: name);

            _approvalLog.Record("enroll", record.Name, _timeProvider.GetUtcNow(), OutcomeSucceeded);
            _logger.LogInformation("Passkey enrolled: {CredentialName}", record.Name);
            return new Contract.PasskeyCredentialResponse { Credential = ToInfo(record) };
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.WebAuthnOptionsResponse> BeginContentUnlock(
        Contract.BeginContentUnlockRequest request,
        ServerCallContext context)
    {
        return Guarded(() =>
        {
            _contentGate.EnsureEnrolled();
            return new Contract.WebAuthnOptionsResponse
            {
                OptionsJson = _ceremonies.BeginAssertion(
                    GatedOperation.ContentUnlock, GatedOperation.ContentUnlockParameters())
            };
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.ContentGrantResponse> FinishContentUnlock(
        Contract.FinishContentUnlockRequest request,
        ServerCallContext context)
    {
        return Guarded(() =>
        {
            _contentGate.EnsureEnrolled();

            var credentialName = RunCeremony(
                () => _ceremonies.FinishAssertion(
                    request.AssertionJson, GatedOperation.ContentUnlock, GatedOperation.ContentUnlockParameters()),
                operation: GatedOperation.ContentUnlock,
                credentialName: string.Empty);

            var token = _contentGrants.IssueGrant();
            var expires = _timeProvider.GetUtcNow().AddMinutes(_options.GetEffectiveContentGrantMinutes());
            _approvalLog.Record(GatedOperation.ContentUnlock, credentialName, _timeProvider.GetUtcNow(),
                OutcomeSucceeded);
            _logger.LogInformation("Content unlocked with passkey {CredentialName}", credentialName);
            return new Contract.ContentGrantResponse
            {
                GrantToken = token,
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(expires)
            };
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.WebAuthnOptionsResponse> BeginOneOperation(
        Contract.BeginOneOperationRequest request,
        ServerCallContext context)
    {
        return Guarded(() =>
        {
            _contentGate.EnsureEnrolled();
            RequireOneOperationName(request.Operation);
            return new Contract.WebAuthnOptionsResponse
            {
                OptionsJson = _ceremonies.BeginAssertion(request.Operation, request.Parameters)
            };
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.OneOperationAuthorizationResponse> FinishOneOperation(
        Contract.FinishOneOperationRequest request,
        ServerCallContext context)
    {
        return Guarded(() =>
        {
            _contentGate.EnsureEnrolled();
            RequireOneOperationName(request.Operation);

            var credentialName = RunCeremony(
                () => _ceremonies.FinishAssertion(request.AssertionJson, request.Operation, request.Parameters),
                operation: request.Operation,
                credentialName: string.Empty);

            var token = _oneOperationAuthorizations.Issue(request.Operation, request.Parameters);
            var now = _timeProvider.GetUtcNow();
            _approvalLog.Record(request.Operation, credentialName, now, OutcomeSucceeded);
            _logger.LogInformation("Passkey approved operation {Operation} with {CredentialName}",
                request.Operation, credentialName);
            return new Contract.OneOperationAuthorizationResponse
            {
                AuthorizationToken = token,
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(now.Add(OneOperationAuthorizationTable.AuthorizationTtl))
            };
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.LockContentResponse> LockContent(
        Contract.LockContentRequest request,
        ServerCallContext context)
    {
        _contentGrants.RevokeAll();
        _approvalLog.Record("lock", string.Empty, _timeProvider.GetUtcNow(), OutcomeSucceeded);
        _logger.LogInformation("Content locked: every content grant revoked");
        return Task.FromResult(new Contract.LockContentResponse());
    }

    /// <inheritdoc/>
    public override Task<Contract.ListRecentApprovalsResponse> ListRecentApprovals(
        Contract.ListRecentApprovalsRequest request,
        ServerCallContext context)
    {
        var response = new Contract.ListRecentApprovalsResponse();
        foreach (var entry in _approvalLog.Recent())
        {
            response.Approvals.Add(new Contract.PasskeyApproval
            {
                Operation = entry.Operation,
                CredentialName = entry.CredentialName,
                AtUtc = Timestamp.FromDateTimeOffset(entry.Utc),
                Outcome = entry.Outcome
            });
        }

        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public override Task<Contract.CreateApprovalRequestResponse> CreateApprovalRequest(
        Contract.CreateApprovalRequestRequest request,
        ServerCallContext context)
    {
        return Guarded(() =>
        {
            // Fails fast with the enrollment message instead of leaving the caller waiting on an approval that
            // no dashboard could ever give.
            _contentGate.EnsureEnrolled();

            if (request.OperationCase != Contract.CreateApprovalRequestRequest.OperationOneofCase.Export)
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "The approval request names no supported operation."));

            var details = request.Export;
            if (string.IsNullOrWhiteSpace(details.DestinationPath))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "An export approval request needs a destination path."));

            var pending = _pendingApprovals.CreateExport(
                ConversationExportWire.ToFilter(details), details.DestinationPath);
            _logger.LogInformation("Approval requested for operation {Operation}", pending.Operation);
            return new Contract.CreateApprovalRequestResponse
            {
                ApprovalId = pending.Id,
                DashboardUrl = _pendingApprovals.DashboardUrlFor(pending.Id),
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(pending.ExpiresAtUtc),
            };
        });
    }

    /// <inheritdoc/>
    public override async Task<Contract.WaitForApprovalResponse> WaitForApproval(
        Contract.WaitForApprovalRequest request,
        ServerCallContext context)
    {
        _contentGate.EnsureEnrolled();

        var decision = await _pendingApprovals.WaitAsync(request.ApprovalId, context.CancellationToken)
            .ConfigureAwait(false)
            ?? throw new RpcException(new Status(StatusCode.NotFound, NoLongerWaitingDetail));

        var response = new Contract.WaitForApprovalResponse
        {
            Outcome = decision.Outcome switch
            {
                PendingApprovalOutcome.Approved => Contract.ApprovalOutcome.Approved,
                PendingApprovalOutcome.Denied => Contract.ApprovalOutcome.Denied,
                _ => Contract.ApprovalOutcome.Expired,
            },
        };

        if (decision.Outcome == PendingApprovalOutcome.Approved)
        {
            // Re-checked at hand-off: the last passkey may have been removed since the operator approved.
            _contentGate.EnsureEnrolled();
            response.AuthorizationToken = _oneOperationAuthorizations.Issue(
                decision.Request.Operation, decision.Request.Parameters);
        }

        _logger.LogInformation("Approval for operation {Operation} collected with outcome {Outcome}",
            decision.Request.Operation, decision.Outcome);
        return response;
    }

    /// <inheritdoc/>
    public override Task<Contract.ListPendingApprovalsResponse> ListPendingApprovals(
        Contract.ListPendingApprovalsRequest request,
        ServerCallContext context)
    {
        var response = new Contract.ListPendingApprovalsResponse();
        foreach (var pending in _pendingApprovals.ListPending())
        {
            response.Approvals.Add(new Contract.PendingApproval
            {
                ApprovalId = pending.Id,
                Export = ConversationExportWire.ToApprovalDetails(pending.Filter, pending.DestinationPath),
                CreatedAtUtc = Timestamp.FromDateTimeOffset(pending.CreatedAtUtc),
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(pending.ExpiresAtUtc),
            });
        }

        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public override Task<Contract.WebAuthnOptionsResponse> BeginApproval(
        Contract.BeginApprovalRequest request,
        ServerCallContext context)
    {
        return Guarded(() =>
        {
            _contentGate.EnsureEnrolled();

            // Operation and digest come from the stored request: the browser cannot choose what it approves.
            var pending = RequirePending(request.ApprovalId);
            return new Contract.WebAuthnOptionsResponse
            {
                OptionsJson = _ceremonies.BeginAssertion(pending.Operation, pending.Parameters)
            };
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.FinishApprovalResponse> FinishApproval(
        Contract.FinishApprovalRequest request,
        ServerCallContext context)
    {
        return Guarded(() =>
        {
            _contentGate.EnsureEnrolled();

            var pending = RequirePending(request.ApprovalId);
            var credentialName = RunCeremony(
                () => _ceremonies.FinishAssertion(request.AssertionJson, pending.Operation, pending.Parameters),
                operation: pending.Operation,
                credentialName: string.Empty);

            // A second dashboard, a denial, or the request lapsing during the ceremony can win the race.
            if (!_pendingApprovals.TryApprove(pending.Id, credentialName))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, NoLongerWaitingDetail));

            _approvalLog.Record(pending.Operation, credentialName, _timeProvider.GetUtcNow(), OutcomeSucceeded);
            _logger.LogInformation("Passkey approved pending operation {Operation} with {CredentialName}",
                pending.Operation, credentialName);
            return new Contract.FinishApprovalResponse();
        });
    }

    /// <inheritdoc/>
    public override Task<Contract.DenyApprovalResponse> DenyApproval(
        Contract.DenyApprovalRequest request,
        ServerCallContext context)
    {
        var pending = _pendingApprovals.TryGetPending(request.ApprovalId);
        if (pending is null || !_pendingApprovals.TryDeny(pending.Id))
            throw new RpcException(new Status(StatusCode.NotFound, NoLongerWaitingDetail));

        _approvalLog.Record(pending.Operation, string.Empty, _timeProvider.GetUtcNow(), OutcomeDenied);
        _logger.LogInformation("Operator denied pending operation {Operation}", pending.Operation);
        return Task.FromResult(new Contract.DenyApprovalResponse());
    }

    /// <summary>Returns the open request with this id, or throws the refusal the approval RPCs share.</summary>
    /// <param name="approvalId">The request's id.</param>
    /// <returns>The request, still waiting for a decision.</returns>
    /// <exception cref="RpcException">With <see cref="StatusCode.NotFound"/> when it is unknown, decided or lapsed.</exception>
    private PendingApprovalRequest RequirePending(string approvalId) =>
        _pendingApprovals.TryGetPending(approvalId)
        ?? throw new RpcException(new Status(StatusCode.NotFound, NoLongerWaitingDetail));

    /// <summary>
    /// Runs <paramref name="body"/> and maps <see cref="PasskeyGateException"/> onto the
    /// <see cref="RpcException"/> the gate's own refusals already use, so callers see one failure shape.
    /// </summary>
    private static Task<T> Guarded<T>(Func<T> body)
    {
        try
        {
            return Task.FromResult(body());
        }
        catch (PasskeyGateException ex)
        {
            throw new RpcException(new Status(ex.StatusCode, ex.Detail));
        }
    }

    /// <summary>
    /// Runs one WebAuthn ceremony step, logging a failed verification and converting the ceremony
    /// service's verification failures (bad JSON, missing or replayed challenge, unknown credential,
    /// signature or counter failure) into <see cref="StatusCode.PermissionDenied"/> without echoing
    /// verifier internals to the caller.
    /// </summary>
    private T RunCeremony<T>(Func<T> ceremony, string operation, string credentialName)
    {
        try
        {
            return ceremony();
        }
        catch (Exception ex) when (ex is Fido2NetLib.Fido2VerificationException or InvalidOperationException
                                       or JsonException or ArgumentException or FormatException
                                       or KeyNotFoundException)
        {
            _approvalLog.Record(operation, credentialName, _timeProvider.GetUtcNow(), OutcomeFailed);
            _logger.LogWarning(ex, "Passkey ceremony for {Operation} failed verification", operation);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Passkey verification failed."));
        }
    }

    /// <summary>
    /// Rejects operation names that cannot be authorized through the one-operation flow:
    /// <see cref="GatedOperation.ContentUnlock"/> issues a grant through its own RPCs, and anything outside
    /// the known set would mint a token no gated RPC ever consumes.
    /// </summary>
    private static void RequireOneOperationName(string operation)
    {
        if (operation is GatedOperation.GetManagementToken or GatedOperation.RegenerateManagementToken
            or GatedOperation.Export or GatedOperation.Import)
        {
            return;
        }

        throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown or unsupported gated operation."));
    }

    private static Contract.PasskeyCredentialInfo ToInfo(PasskeyCredentialRecord record) => new()
    {
        Id = PasskeyEncoding.ToBase64Url(record.Id),
        Name = record.Name,
        CreatedAtUtc = Timestamp.FromDateTimeOffset(record.CreatedAtUtc),
        BackupEligible = record.BackupEligible,
        BackupState = record.BackupState
    };
}
