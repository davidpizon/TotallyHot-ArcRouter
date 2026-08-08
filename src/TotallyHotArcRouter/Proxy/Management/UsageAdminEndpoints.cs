using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Maps the <c>/admin/usage/*</c> query surface (Phase 4, §5.15) the Governance/Model Distribution/Cost
/// Analytics GUI tabs read Phase 4 rollups through. The GUI only ever talks to the proxy
/// (<c>docs/router/telemetry.md#gui-consumption</c>) - this is the "new REST endpoint... served by the
/// proxy" that principle requires, never a direct read of <c>agent_telemetry.db</c>. All logic lives in
/// <see cref="ManagementFacade"/>; this file only translates HTTP requests into facade calls and
/// <see cref="ManagementResult{T}"/> outcomes into HTTP responses, mirroring
/// <see cref="ProviderAdminEndpoints"/>.
/// </summary>
public static class UsageAdminEndpoints
{
    private const string TokenHeaderName = "X-Admin-Token";

    /// <summary>
    /// Maps the <c>/admin/usage/*</c> endpoints onto <paramref name="endpoints"/>, gated by the same
    /// <c>X-Admin-Token</c> header as every other <c>/admin/*</c> route.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (the proxy's inner Kestrel host).</param>
    /// <param name="facade">The shared management facade backing every read.</param>
    /// <param name="managementToken">
    /// Optional shared secret; when non-empty, every <c>/admin/usage/*</c> request must present it in the
    /// <c>X-Admin-Token</c> header or receive a 401. See <see cref="ProviderAdminEndpoints.MapProviderAdminEndpoints"/>
    /// for the identical rationale.
    /// </param>
    public static IEndpointRouteBuilder MapUsageAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        ManagementFacade facade,
        string? managementToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(facade);

        var group = endpoints.MapGroup("/admin/usage");

        if (!string.IsNullOrWhiteSpace(managementToken))
        {
            group.AddEndpointFilter(async (context, next) =>
            {
                var provided = context.HttpContext.Request.Headers[TokenHeaderName].ToString();
                if (!ManagementAccessToken.Verify(provided, managementToken))
                {
                    return Error(StatusCodes.Status401Unauthorized, "Missing or invalid management token.", "unauthorized");
                }

                return await next(context);
            });
        }

        // Totals for the header ticker and summary tiles - a preset window rather than an explicit range,
        // since every caller of this endpoint wants "the last day/week/month/all-time", not an arbitrary span.
        group.MapGet("/summary", (string? window) =>
            ToResult(facade.GetUsageSummary(window ?? "day")));

        // The Model Distribution / Cost Analytics chart feed - an explicit range, since these callers drive
        // a filter bar the operator controls directly.
        group.MapGet("/rollup", (string? from, string? to, string? width, string? groupBy) =>
        {
            if (!TryParseInstant(from, out var fromInstant))
            {
                return Error(StatusCodes.Status400BadRequest, "'from' must be a valid ISO 8601 instant.", "invalid_request_error");
            }

            if (!TryParseInstant(to, out var toInstant))
            {
                return Error(StatusCodes.Status400BadRequest, "'to' must be a valid ISO 8601 instant.", "invalid_request_error");
            }

            return ToResult(facade.GetUsageRollup(fromInstant, toInstant, width ?? "day", groupBy ?? "day"));
        });

        return endpoints;
    }

    private static bool TryParseInstant(string? value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant);

    private static IResult ToResult<T>(ManagementResult<T> result) =>
        result.Success
            ? Results.Ok(result.Value)
            : result.ErrorType switch
            {
                ManagementErrorType.NotFound => Error(StatusCodes.Status404NotFound, result.ErrorMessage!, "not_found"),
                ManagementErrorType.InvalidRequest => Error(StatusCodes.Status400BadRequest, result.ErrorMessage!, "invalid_request_error"),
                ManagementErrorType.Unavailable => Error(StatusCodes.Status503ServiceUnavailable, result.ErrorMessage!, "unavailable"),
                _ => Error(StatusCodes.Status500InternalServerError, result.ErrorMessage!, "internal_error"),
            };

    /// <summary>Builds an OpenAI-shaped JSON error response with the given status code, message, and error type.</summary>
    private static IResult Error(int statusCode, string message, string type) =>
        Results.Json(
            new { error = new { message, type, code = statusCode.ToString(CultureInfo.InvariantCulture) } },
            statusCode: statusCode);
}
