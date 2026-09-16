using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// gRPC service backing the System Settings window's "Copy MCP token / Regenerate" row (web GUI migration
/// plan Phase P9): reads and rotates the shared bearer token every gRPC admin service and MCP gate behind.
/// Mapped by <see cref="ProxyServer"/> onto the same loopback TLS endpoint as <c>TelemetryService</c> and
/// the other admin services, only when a real <see cref="IManagementTokenProvider"/> is configured -
/// mirroring <c>ManagementAuthEndpoints</c>' own condition, since a router with no inbound auth
/// configured has no token to administer.
/// </summary>
public sealed class ManagementTokenAdminGrpcService : Contract.ManagementTokenAdminService.ManagementTokenAdminServiceBase
{
    private readonly IManagementTokenProvider _tokenProvider;

    /// <summary>Initializes a new instance of the <see cref="ManagementTokenAdminGrpcService"/> class.</summary>
    public ManagementTokenAdminGrpcService(IManagementTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
    }

    /// <inheritdoc/>
    public override Task<Contract.ManagementTokenResponse> GetManagementToken(
        Contract.GetManagementTokenRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new Contract.ManagementTokenResponse { Token = _tokenProvider.CurrentToken });
    }

    /// <inheritdoc/>
    public override Task<Contract.ManagementTokenResponse> RegenerateManagementToken(
        Contract.RegenerateManagementTokenRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new Contract.ManagementTokenResponse { Token = _tokenProvider.Regenerate() });
    }
}
