using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// Client for the proxy's <c>PasskeyAdminService</c> - ADR-0020's passkey content gate. Lives in this plain
/// <c>net10.0</c> library rather than a UI-framework project so CI can unit-test the wire-to-view mapping,
/// exactly like <see cref="ManagementTokenAdminClient"/>. Every method wraps a failed call in a
/// <see cref="GrpcAdminException"/> carrying the router's own detail (for instance the enrollment-required
/// message that names the elevated enrollment command), since that detail is what the operator needs to act on.
/// </summary>
public sealed class PasskeyAdminClient
    : GrpcAdminClientBase<Contract.PasskeyAdminService.PasskeyAdminServiceClient>,
        IPasskeyAdminClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyAdminClient"/> class over a shared, already-
    /// authenticated call invoker - see <see cref="IRouterChannelProvider"/>'s remarks.
    /// </summary>
    /// <param name="callInvoker">The shared call invoker - see <see cref="IRouterChannelProvider.CallInvoker"/>.</param>
    public PasskeyAdminClient(CallInvoker callInvoker)
        : base(new Contract.PasskeyAdminService.PasskeyAdminServiceClient(callInvoker))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyAdminClient"/> class over a caller-supplied
    /// generated client. The seam tests use to substitute a fake without a live server; the caller owns the
    /// channel's lifetime.
    /// </summary>
    /// <param name="client">The generated client to wrap.</param>
    public PasskeyAdminClient(Contract.PasskeyAdminService.PasskeyAdminServiceClient client)
        : base(client)
    {
    }

    /// <inheritdoc/>
    public async Task<PasskeyGateStatusInfo> GetGateStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(
            call: (client, ct) => client.GetPasskeyGateStatusAsync(
                request: new Contract.GetPasskeyGateStatusRequest(), cancellationToken: ct),
            action: "Could not read the passkey gate status",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new PasskeyGateStatusInfo(
            StoreProtected: response.StoreProtected,
            Enrolled: response.Enrolled,
            GrantActive: response.GrantActive,
            GrantExpiresAtUtc: response.GrantExpiresAtUtc?.ToDateTimeOffset(),
            EnrollmentCommandHint: string.IsNullOrWhiteSpace(response.EnrollmentCommandHint)
                ? PasskeyOperations.EnrollmentCommandFlag
                : response.EnrollmentCommandHint);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PasskeyInfo>> ListPasskeysAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(
            call: (client, ct) => client.ListPasskeysAsync(
                request: new Contract.ListPasskeysRequest(), cancellationToken: ct),
            action: "Could not list the passkeys",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return [.. response.Credentials.Select(ToInfo)];
    }

    /// <inheritdoc/>
    public async Task<string> BeginEnrollmentAsync(string enrollmentCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enrollmentCode);

        var response = await CallAsync(
            call: (client, ct) => client.BeginEnrollmentAsync(
                request: new Contract.BeginEnrollmentRequest { EnrollmentCode = enrollmentCode },
                cancellationToken: ct),
            action: "Could not start the passkey enrollment",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.OptionsJson;
    }

    /// <inheritdoc/>
    public async Task<PasskeyInfo> FinishEnrollmentAsync(string attestationJson, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attestationJson);
        ArgumentNullException.ThrowIfNull(name);

        var response = await CallAsync(
            call: (client, ct) => client.FinishEnrollmentAsync(
                request: new Contract.FinishEnrollmentRequest { AttestationJson = attestationJson, Name = name },
                cancellationToken: ct),
            action: "Could not finish the passkey enrollment",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToInfo(response.Credential);
    }

    /// <inheritdoc/>
    public async Task<string> BeginContentUnlockAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(
            call: (client, ct) => client.BeginContentUnlockAsync(
                request: new Contract.BeginContentUnlockRequest(), cancellationToken: ct),
            action: "Could not start the content unlock",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.OptionsJson;
    }

    /// <inheritdoc/>
    public async Task<ContentGrantInfo> FinishContentUnlockAsync(string assertionJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertionJson);

        var response = await CallAsync(
            call: (client, ct) => client.FinishContentUnlockAsync(
                request: new Contract.FinishContentUnlockRequest { AssertionJson = assertionJson },
                cancellationToken: ct),
            action: "Could not finish the content unlock",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ContentGrantInfo(
            GrantToken: response.GrantToken,
            ExpiresAtUtc: response.ExpiresAtUtc.ToDateTimeOffset());
    }

    /// <inheritdoc/>
    public async Task<string> BeginOneOperationAsync(string operation, string parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(parameters);

        var response = await CallAsync(
            call: (client, ct) => client.BeginOneOperationAsync(
                request: new Contract.BeginOneOperationRequest { Operation = operation, Parameters = parameters },
                cancellationToken: ct),
            action: "Could not start the passkey verification",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.OptionsJson;
    }

    /// <inheritdoc/>
    public async Task<OneOperationAuthorizationInfo> FinishOneOperationAsync(string assertionJson, string operation,
        string parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertionJson);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(parameters);

        var response = await CallAsync(
            call: (client, ct) => client.FinishOneOperationAsync(
                request: new Contract.FinishOneOperationRequest
                {
                    AssertionJson = assertionJson,
                    Operation = operation,
                    Parameters = parameters
                },
                cancellationToken: ct),
            action: "Could not finish the passkey verification",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new OneOperationAuthorizationInfo(
            AuthorizationToken: response.AuthorizationToken,
            ExpiresAtUtc: response.ExpiresAtUtc.ToDateTimeOffset());
    }

    /// <inheritdoc/>
    public async Task LockContentAsync(CancellationToken cancellationToken = default)
    {
        await CallAsync(
            call: (client, ct) => client.LockContentAsync(
                request: new Contract.LockContentRequest(), cancellationToken: ct),
            action: "Could not lock the conversation content",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PasskeyApprovalInfo>> ListRecentApprovalsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(
            call: (client, ct) => client.ListRecentApprovalsAsync(
                request: new Contract.ListRecentApprovalsRequest(), cancellationToken: ct),
            action: "Could not read the recent passkey approvals",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return
        [
            .. response.Approvals.Select(a => new PasskeyApprovalInfo(
                Operation: a.Operation,
                CredentialName: a.CredentialName,
                AtUtc: a.AtUtc.ToDateTimeOffset(),
                Outcome: a.Outcome))
        ];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingApprovalInfo>> ListPendingApprovalsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(
            call: (client, ct) => client.ListPendingApprovalsAsync(
                request: new Contract.ListPendingApprovalsRequest(), cancellationToken: ct),
            action: "Could not read the pending approval requests",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return [.. response.Approvals.Select(ToInfo)];
    }

    /// <inheritdoc/>
    public async Task<string> BeginApprovalAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);

        var response = await CallAsync(
            call: (client, ct) => client.BeginApprovalAsync(
                request: new Contract.BeginApprovalRequest { ApprovalId = approvalId }, cancellationToken: ct),
            action: "Could not start the passkey verification",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.OptionsJson;
    }

    /// <inheritdoc/>
    public async Task FinishApprovalAsync(string approvalId, string assertionJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentNullException.ThrowIfNull(assertionJson);

        await CallAsync(
            call: (client, ct) => client.FinishApprovalAsync(
                request: new Contract.FinishApprovalRequest { ApprovalId = approvalId, AssertionJson = assertionJson },
                cancellationToken: ct),
            action: "Could not finish the passkey verification",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DenyApprovalAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);

        await CallAsync(
            call: (client, ct) => client.DenyApprovalAsync(
                request: new Contract.DenyApprovalRequest { ApprovalId = approvalId }, cancellationToken: ct),
            action: "Could not deny the approval request",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Converts a wire <see cref="Contract.PendingApproval"/> into a <see cref="PendingApprovalInfo"/>.</summary>
    private static PendingApprovalInfo ToInfo(Contract.PendingApproval pending)
    {
        var export = pending.Export ?? new Contract.ExportApprovalDetails();
        return new PendingApprovalInfo(
            ApprovalId: pending.ApprovalId,
            Filter: new ConversationExportFilterInfo(
                From: export.FromUtc?.ToDateTimeOffset(),
                To: export.ToUtc?.ToDateTimeOffset(),
                SessionId: export.HasSessionId ? export.SessionId : null,
                Harness: export.HasHarness ? export.Harness : null,
                Provider: export.HasProvider ? export.Provider : null,
                Model: export.HasModel ? export.Model : null),
            DestinationPath: export.DestinationPath,
            CreatedAtUtc: pending.CreatedAtUtc.ToDateTimeOffset(),
            ExpiresAtUtc: pending.ExpiresAtUtc.ToDateTimeOffset());
    }

    /// <summary>Converts a wire <see cref="Contract.PasskeyCredentialInfo"/> into a <see cref="PasskeyInfo"/>.</summary>
    private static PasskeyInfo ToInfo(Contract.PasskeyCredentialInfo credential)
    {
        return new PasskeyInfo(
            Id: credential.Id,
            Name: credential.Name,
            CreatedAtUtc: credential.CreatedAtUtc.ToDateTimeOffset(),
            BackupEligible: credential.BackupEligible,
            BackupState: credential.BackupState);
    }
}
