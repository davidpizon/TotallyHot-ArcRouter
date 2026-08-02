using TotallyHot.ArcRouter.Proxy.Management;
using Microsoft.AspNetCore.Http;

namespace TotallyHot.ArcRouter.Mcp;

/// <summary>
/// Gates every request to the MCP endpoint behind the shared per-user management token
/// (<see cref="ManagementAccessToken"/> - the same token the hardened REST <c>/admin/*</c> API requires),
/// presented as an <c>Authorization: Bearer &lt;token&gt;</c> header. Placed before <c>MapMcp()</c> in the
/// MCP host's pipeline (<see cref="McpServer"/>), so every tool call - list or mutate - is authenticated;
/// TLS on this port is defense-in-depth on top, not a substitute (see
/// <see cref="TotallyHot.ArcRouter.Telemetry.TelemetryTlsCertificate"/>'s remarks on the loopback trust model).
/// </summary>
public sealed class McpBearerAuthMiddleware
{
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly string _expectedToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpBearerAuthMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="expectedToken">The token every request must present.</param>
    public McpBearerAuthMiddleware(RequestDelegate next, string expectedToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToken);

        _next = next;
        _expectedToken = expectedToken;
    }

    /// <summary>Verifies the bearer token and either forwards the request or responds with 401.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The "Bearer" scheme name is case-insensitive per RFC 6750/9110, and a client may pad the token
        // with incidental whitespace - neither should cause a surprising 401.
        var header = context.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;

        if (!ManagementAccessToken.Verify(presented, _expectedToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":{"message":"Missing or invalid bearer token.","type":"unauthorized","code":"401"}}""",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}

