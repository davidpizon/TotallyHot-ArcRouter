using Grpc.Core;
using Grpc.Core.Interceptors;
using TotallyHot.ArcRouter.Proxy.Auth;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Gates every call to the telemetry gRPC endpoint - <see cref="TelemetryGrpcService"/> and
/// <see cref="TotallyHot.ArcRouter.PriceCatalog.PriceSourceAdminGrpcService"/>, which share the same TLS
/// port - behind either the shared management token (the <c>x-admin-token</c> metadata entry, unchanged
/// since before Phase P4) or the ADR-0012 session cookie a gRPC-Web browser call carries automatically.
/// This is the same pattern the MCP endpoint's bearer token uses
/// (<see cref="TotallyHot.ArcRouter.Mcp.McpBearerAuthMiddleware"/>), both backed by the same rotatable
/// <see cref="IManagementTokenProvider"/> so every management surface is gated identically and a
/// <see cref="IManagementTokenProvider.Regenerate"/> call takes effect everywhere without a restart. TLS
/// on this port is defense-in-depth on top of this check, not a substitute for it - see
/// <see cref="LocalCertificateAuthority"/>'s remarks on the loopback trust model, and
/// docs/router/signalr-hub-security.md §2 for the original (SignalR-era) design this translates.
/// </summary>
public sealed class TelemetryAuthInterceptor : Interceptor
{
    private const string TokenHeaderName = "x-admin-token";

    private readonly ManagementSessionTicketService _sessionTickets;
    private readonly IManagementTokenProvider _tokenProvider;

    /// <summary>Initializes a new instance of the <see cref="TelemetryAuthInterceptor"/> class.</summary>
    /// <param name="tokenProvider">Verifies the <c>x-admin-token</c> metadata entry.</param>
    /// <param name="sessionTickets">Verifies the ADR-0012 session cookie, when the token is absent.</param>
    public TelemetryAuthInterceptor(IManagementTokenProvider tokenProvider, ManagementSessionTicketService sessionTickets)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(sessionTickets);
        _tokenProvider = tokenProvider;
        _sessionTickets = sessionTickets;
    }

    /// <inheritdoc/>
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request: request, context: context);
    }

    /// <inheritdoc/>
    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request: request, responseStream: responseStream, context: context);
    }

    /// <inheritdoc/>
    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream: requestStream, context: context);
    }

    /// <inheritdoc/>
    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream: requestStream, responseStream: responseStream, context: context);
    }

    /// <summary>
    /// Verifies the call's <c>x-admin-token</c> metadata entry against the current management token, or
    /// (when absent or wrong) falls back to the ADR-0012 session cookie a gRPC-Web browser call carries
    /// on its underlying <see cref="HttpContext"/>.
    /// </summary>
    /// <exception cref="RpcException">Neither credential is present or valid, with <see cref="StatusCode.Unauthenticated"/>.</exception>
    private void Authenticate(ServerCallContext context)
    {
        var presented = context.RequestHeaders
            .FirstOrDefault(entry =>
                string.Equals(a: entry.Key, b: TokenHeaderName, comparisonType: StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (_tokenProvider.Verify(presented)) return;

        if (_sessionTickets.IsValid(TryReadSessionCookie(context))) return;

        throw new RpcException(new Status(statusCode: StatusCode.Unauthenticated,
            detail: "Missing or invalid management token or session."));
    }

    /// <summary>
    /// Reads the ADR-0012 session cookie off this call's underlying <see cref="HttpContext"/>, or
    /// <see langword="null"/> when none is attached - <c>Grpc.Core.Testing.TestServerCallContext</c> (used
    /// by this class's unit tests) has no supported way to attach one, unlike production's
    /// <c>Grpc.AspNetCore.Server</c> pipeline, which always does and never throws here.
    /// </summary>
    private static string? TryReadSessionCookie(ServerCallContext context)
    {
        try
        {
            return ManagementSessionCookie.Read(context.GetHttpContext().Request);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
