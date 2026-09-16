using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Proxy.Auth;

/// <summary>
/// Maps the three ADR-0012 session endpoints on the web port: <c>POST /auth/session</c> (loopback
/// fast-path issuance), <c>POST /auth/login</c> (token login for non-loopback callers), and
/// <c>POST /auth/logout</c>. Only mapped when the inner host was given a
/// <see cref="IManagementTokenProvider"/> (see <see cref="ProxyServer"/>'s constructor) - a caller with
/// no token configured is a test exercising plain forwarding, which has nothing for these to gate.
/// </summary>
public static class ManagementAuthEndpoints
{
    /// <summary>Registers the three endpoints against <paramref name="endpoints"/>.</summary>
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(pattern: "/auth/session", requestDelegate: HandleSessionAsync);
        endpoints.MapPost(pattern: "/auth/login", requestDelegate: HandleLoginAsync);
        endpoints.MapPost(pattern: "/auth/logout", requestDelegate: HandleLogoutAsync);
    }

    /// <summary>
    /// Loopback fast-path issuance: 204 with a fresh session cookie when the caller's remote IP is
    /// loopback (Host/Origin were already checked by <see cref="WebPortRequestGuardMiddleware"/> earlier
    /// in the pipeline); 403 otherwise, telling the caller to use <c>/auth/login</c> instead.
    /// </summary>
    private static Task HandleSessionAsync(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<WebInterfaceOptions>();
        var ticketService = context.RequestServices.GetRequiredService<ManagementSessionTicketService>();

        if (!options.TrustLoopback || !LoopbackRequestGuard.IsLoopback(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        ManagementSessionCookie.Write(response: context.Response, ticket: ticketService.IssueTicket());
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Token login for non-loopback callers (Docker, <c>WebInterface:TrustLoopback=false</c>): verifies
    /// a JSON <c>{"token":"..."}</c> body against the current management token, rate-limited per remote
    /// address via <see cref="LoginRateLimiter"/>.
    /// </summary>
    private static async Task HandleLoginAsync(HttpContext context)
    {
        var tokenProvider = context.RequestServices.GetRequiredService<IManagementTokenProvider>();
        var ticketService = context.RequestServices.GetRequiredService<ManagementSessionTicketService>();
        var rateLimiter = context.RequestServices.GetRequiredService<LoginRateLimiter>();

        var rateLimitKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (rateLimiter.ShouldThrottle(rateLimitKey))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        LoginRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<LoginRequest>(context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            request = null;
        }

        if (!tokenProvider.Verify(request?.Token))
        {
            rateLimiter.RecordFailure(rateLimitKey);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        rateLimiter.RecordSuccess(rateLimitKey);
        ManagementSessionCookie.Write(response: context.Response, ticket: ticketService.IssueTicket());
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>Clears the session cookie unconditionally. Always 204 - logging out an already-anonymous caller is not an error.</summary>
    private static Task HandleLogoutAsync(HttpContext context)
    {
        ManagementSessionCookie.Clear(context.Response);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    /// <summary>The <c>POST /auth/login</c> request body.</summary>
    /// <param name="Token">The management token being presented.</param>
    private sealed record LoginRequest(string? Token);
}
