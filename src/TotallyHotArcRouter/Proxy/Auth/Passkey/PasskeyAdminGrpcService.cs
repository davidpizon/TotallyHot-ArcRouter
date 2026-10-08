using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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
public sealed class PasskeyAdminGrpcService : Contract.PasskeyAdminService.PasskeyAdminServiceBase
{
    private const string OutcomeSucceeded = "succeeded";
    private const string OutcomeFailed = "failed";

    private readonly ContentGate _contentGate;
    private readonly EnrollmentCodeService _enrollmentCodes;
    private readonly IWebAuthnCeremonyService _ceremonies;
    private readonly ContentGrantTable _contentGrants;
    private readonly OneOperationAuthorizationTable _oneOperationAuthorizations;
    private readonly PasskeyApprovalLog _approvalLog;
    private readonly IPasskeyCredentialStore _credentialStore;
    private readonly PasskeyOptions _options;
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
        TimeProvider? timeProvider = null,
        ILogger<PasskeyAdminGrpcService>? logger = null)
    {
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
            GrantActive = _contentGate.TryGetContentGrant(context, out _),
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
