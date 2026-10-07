namespace TotallyHot.ArcRouter.Gui.Telemetry;

/// <summary>
/// The operation names the router's passkey gate recognizes for one-operation authorizations (ADR-0020).
/// Duplicated from the router's <c>GatedOperation</c> constants because the GUI cannot reference the router
/// project - the same "one fact, two copies across a process boundary" tradeoff
/// <see cref="TelemetryChannelFactory.ValidateLoopbackCertificate"/> accepts. The values travel on the wire
/// in <c>BeginOneOperationRequest.operation</c> and must match the router's strings exactly.
/// </summary>
public static class PasskeyOperations
{
    /// <summary>Authorizes one <c>ManagementTokenAdminService.GetManagementToken</c> call.</summary>
    public const string GetManagementToken = "get_management_token";

    /// <summary>Authorizes one <c>ManagementTokenAdminService.RegenerateManagementToken</c> call.</summary>
    public const string RegenerateManagementToken = "regenerate_management_token";

    /// <summary>
    /// The parameter string bound to the operations above. Neither takes parameters, so the router binds
    /// them with an empty string and the GUI must send exactly that.
    /// </summary>
    public const string NoParameters = "";

    /// <summary>
    /// The command-line flag an operator runs from an elevated shell to mint a single-use enrollment code,
    /// used as the fallback hint when the router's gate status does not supply one.
    /// </summary>
    public const string EnrollmentCommandFlag = "--mint-passkey-enrollment-code";

    /// <summary>
    /// The command-line flag prefix an operator runs from an elevated shell to remove a passkey (ADR-0020
    /// decision 4: removal is elevated-channel only, never a dashboard action).
    /// </summary>
    public const string RevokeCommandFlag = "--revoke-passkey=";
}
