namespace TotallyHot.ArcRouter.Proxy.Auth;

/// <summary>
/// ADR-0012's per-request guard: rejects any request on the web-port pipeline whose <c>Host</c>
/// header is not in <see cref="WebInterfaceOptions.AllowedHosts"/> (DNS-rebinding defense), or whose
/// <c>Origin</c> header (when present) does not match the request's own origin (CSRF defense). Applied
/// unconditionally to every web-port connection - native gRPC callers included, since they share this
/// same Kestrel pipeline (Phase P9 retired the formerly-dedicated native-gRPC port) and typically send
/// neither header, which both checks treat as acceptable.
/// </summary>
/// <remarks>
/// Deliberately never enables ASP.NET Core's <c>ForwardedHeaders</c> middleware (ADR-0012): trusting a
/// client-supplied <c>X-Forwarded-*</c> header for this decision would let exactly the DNS-rebinding
/// attacker this guard exists to stop simply claim to be forwarded from wherever they like.
/// </remarks>
public sealed class WebPortRequestGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly WebInterfaceOptions _options;

    /// <summary>Initializes a new instance of the <see cref="WebPortRequestGuardMiddleware"/> class.</summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="options">The web interface's Host allowlist.</param>
    public WebPortRequestGuardMiddleware(RequestDelegate next, WebInterfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _options = options;
    }

    /// <summary>Applies the Host/Origin checks and either forwards the request or responds with 403.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!LoopbackRequestGuard.IsHostAllowed(context.Request.Host.Host, _options.AllowedHosts))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!LoopbackRequestGuard.IsOriginAllowed(
                origin: string.IsNullOrEmpty(origin) ? null : origin,
                secFetchSitePresent: context.Request.Headers.ContainsKey("Sec-Fetch-Site"),
                expectedOrigin: expectedOrigin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
