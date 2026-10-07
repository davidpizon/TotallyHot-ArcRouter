using Grpc.Core;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// gRPC service backing the System Settings window's "Copy MCP token / Regenerate" row (web GUI migration
/// plan Phase P9): reads and rotates the shared bearer token every gRPC admin service and MCP gate behind.
/// Mapped by <see cref="ProxyServer"/> onto the same loopback TLS endpoint as <c>TelemetryService</c> and
/// the other admin services, only when a real <see cref="IManagementTokenProvider"/> is configured -
/// mirroring <c>ManagementAuthEndpoints</c>' own condition, since a router with no inbound auth
/// configured has no token to administer. Both RPCs are gated by ADR-0020: each consumes a one-operation
/// passkey authorization (<c>authorization_token</c>) before touching the token, so a stolen session cannot
/// read or rotate it without a fresh passkey approval.
/// </summary>
public sealed class ManagementTokenAdminGrpcService : Contract.ManagementTokenAdminService.ManagementTokenAdminServiceBase
{
    private readonly IManagementTokenProvider _tokenProvider;
    private readonly ContentGate _contentGate;

    /// <summary>Initializes a new instance of the <see cref="ManagementTokenAdminGrpcService"/> class.</summary>
    /// <param name="tokenProvider">The shared, rotatable management token.</param>
    /// <param name="contentGate">Verifies and consumes the one-operation passkey authorization each RPC requires.</param>
    public ManagementTokenAdminGrpcService(IManagementTokenProvider tokenProvider, ContentGate contentGate)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(contentGate);
        _tokenProvider = tokenProvider;
        _contentGate = contentGate;
    }

    /// <inheritdoc/>
    public override Task<Contract.ManagementTokenResponse> GetManagementToken(
        Contract.GetManagementTokenRequest request,
        ServerCallContext context)
    {
        _contentGate.RequireAndConsumeOneOperation(
            authorizationToken: request.AuthorizationToken,
            operation: GatedOperation.GetManagementToken,
            parameters: GatedOperation.GetManagementTokenParameters());
        return Task.FromResult(new Contract.ManagementTokenResponse { Token = _tokenProvider.CurrentToken });
    }

    /// <inheritdoc/>
    public override Task<Contract.ManagementTokenResponse> RegenerateManagementToken(
        Contract.RegenerateManagementTokenRequest request,
        ServerCallContext context)
    {
        _contentGate.RequireAndConsumeOneOperation(
            authorizationToken: request.AuthorizationToken,
            operation: GatedOperation.RegenerateManagementToken,
            parameters: GatedOperation.RegenerateManagementTokenParameters());
        return Task.FromResult(new Contract.ManagementTokenResponse { Token = _tokenProvider.Regenerate() });
    }
}
