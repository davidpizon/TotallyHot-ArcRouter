using Grpc.Core;
using Grpc.Core.Interceptors;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Gates every call to the telemetry gRPC endpoint - <see cref="TelemetryGrpcService"/> and
/// <see cref="TotallyHot.ArcRouter.PriceCatalog.PriceSourceAdminGrpcService"/>, which share the same TLS
/// port - behind the shared per-user management token, presented in the <c>x-admin-token</c> metadata
/// entry. This is the gRPC analog of the REST <c>/admin/*</c> API's <c>X-Admin-Token</c> header
/// (<see cref="ProviderAdminEndpoints"/>) and the MCP endpoint's bearer token
/// (<see cref="TotallyHot.ArcRouter.Mcp.McpBearerAuthMiddleware"/>), all backed by the same
/// <see cref="ManagementAccessToken"/> so every management surface is gated identically. TLS on this
/// port is defense-in-depth on top of this check, not a substitute for it - see
/// <see cref="TelemetryTlsCertificate"/>'s remarks on the loopback trust model, and
/// docs/router/signalr-hub-security.md §2 for the original (SignalR-era) design this translates.
/// </summary>
public sealed class TelemetryAuthInterceptor : Interceptor
{
    private const string TokenHeaderName = "x-admin-token";

    private readonly string _expectedToken;

    /// <summary>Initializes a new instance of the <see cref="TelemetryAuthInterceptor"/> class.</summary>
    /// <param name="expectedToken">The token every call must present.</param>
    public TelemetryAuthInterceptor(string expectedToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToken);
        _expectedToken = expectedToken;
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
    /// Verifies the call's <c>x-admin-token</c> metadata entry against the expected token.
    /// </summary>
    /// <exception cref="RpcException">The token is missing or doesn't match, with <see cref="StatusCode.Unauthenticated"/>.</exception>
    private void Authenticate(ServerCallContext context)
    {
        var presented = context.RequestHeaders
            .FirstOrDefault(entry =>
                string.Equals(a: entry.Key, b: TokenHeaderName, comparisonType: StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!ManagementAccessToken.Verify(presented: presented, expected: _expectedToken))
            throw new RpcException(new Status(statusCode: StatusCode.Unauthenticated,
                detail: "Missing or invalid management token."));
    }
}