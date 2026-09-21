using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Mcp;

/// <summary>
/// Gates every request to the MCP endpoint behind the shared, rotatable management token
/// (<see cref="IManagementTokenProvider"/> - the same token the TLS gRPC endpoint requires), presented as
/// an <c>Authorization: Bearer &lt;token&gt;</c> header. Placed before <c>MapMcp()</c> in the MCP host's
/// pipeline (<see cref="McpServer"/>), so every tool call - list or mutate - is authenticated; TLS on
/// this port is defense-in-depth on top, not a substitute (see
/// <see cref="TotallyHot.ArcRouter.Telemetry.LocalCertificateAuthority"/>'s remarks on the loopback trust model).
/// Reads <see cref="IManagementTokenProvider.CurrentToken"/> on every request rather than a captured
/// string, so a management-token rotation (Phase P4) takes effect on this endpoint immediately.
/// </summary>
public sealed class McpBearerAuthMiddleware
{
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly IManagementTokenProvider _tokenProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpBearerAuthMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="tokenProvider">Verifies the presented token against the current management token.</param>
    public McpBearerAuthMiddleware(RequestDelegate next, IManagementTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        _next = next;
        _tokenProvider = tokenProvider;
    }

    /// <summary>Verifies the bearer token and either forwards the request or responds with 401.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The "Bearer" scheme name is case-insensitive per RFC 6750/9110, and a client may pad the token
        // with incidental whitespace - neither should cause a surprising 401.
        var header = context.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith(value: BearerPrefix, comparisonType: StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;

        if (!_tokenProvider.Verify(presented))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":{"message":"Missing or invalid bearer token.","type":"unauthorized","code":"401"}}""",
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
