namespace TotallyHot.ArcRouter.Proxy.Auth;

/// <summary>
/// Reads and writes the <c>__Host-</c>-prefixed session cookie ADR-0012 issues from
/// <c>POST /auth/session</c> and <c>POST /auth/login</c>. Centralized here so the cookie's name and
/// attributes are defined exactly once and cannot drift between the issuing endpoints, the clearing
/// endpoint, and <see cref="TotallyHot.ArcRouter.Telemetry.TelemetryAuthInterceptor"/>'s read path.
/// </summary>
public static class ManagementSessionCookie
{
    /// <summary>
    /// The cookie name. The <c>__Host-</c> prefix is a browser-enforced guarantee (RFC 6265bis) that the
    /// cookie was set with <c>Secure</c>, <c>Path=/</c>, and no <c>Domain</c> attribute - exactly the
    /// three attributes <see cref="Write"/> sets below - so a network attacker who can inject a cookie
    /// for a sibling subdomain (there are none here, but the guarantee is unconditional) cannot forge
    /// one that overrides this one.
    /// </summary>
    private const string Name = "__Host-arcrouter-session";

    /// <summary>Sets the session cookie on <paramref name="response"/> with the required <c>__Host-</c> attributes.</summary>
    public static void Write(HttpResponse response, string ticket)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);

        response.Cookies.Append(key: Name, value: ticket, options: new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }

    /// <summary>Clears the session cookie, e.g. from <c>POST /auth/logout</c>.</summary>
    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(key: Name, options: new CookieOptions { Secure = true, Path = "/" });
    }

    /// <summary>Reads the session cookie's raw ticket value from <paramref name="request"/>, or <see langword="null"/> if absent.</summary>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Cookies.TryGetValue(key: Name, value: out var ticket) ? ticket : null;
    }
}
