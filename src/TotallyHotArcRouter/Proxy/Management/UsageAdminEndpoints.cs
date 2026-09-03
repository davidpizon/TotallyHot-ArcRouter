using System.Globalization;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Maps the <c>/admin/usage/*</c> query surface (Phase 4, §5.15) the Governance/Model Distribution/Cost
/// Analytics GUI tabs read Phase 4 rollups through. The GUI only ever talks to the proxy
/// (<c>docs/router/telemetry.md#gui-consumption</c>) - this is the "new REST endpoint... served by the
/// proxy" that principle requires, never a direct read of <c>agent_telemetry.db</c>. All logic lives in
/// <see cref="ManagementReportingService"/>; this file only translates HTTP requests into service calls
/// and <see cref="ManagementResult{T}"/> outcomes into HTTP responses, mirroring
/// <see cref="ProviderAdminEndpoints"/>.
/// </summary>
public static class UsageAdminEndpoints
{
    private const string TokenHeaderName = "X-Admin-Token";

    // Exact ISO 8601 shapes only, not DateTimeOffset.TryParse's general (culture-permissive, locale-shape)
    // parsing - the 400 error message promises "a valid ISO 8601 instant", and TryParse alone would silently
    // accept many non-ISO formats too. UsageQueryClient.GetRollupAsync always sends the round-trip ("O")
    // format; the other two cover the common no-fractional/millisecond ISO instant shapes other callers of
    // this REST endpoint might reasonably send.
    private static readonly string[] IsoInstantFormats =
    [
        "yyyy-MM-ddTHH:mm:ss.fffffffK",
        "yyyy-MM-ddTHH:mm:ss.fffK",
        "yyyy-MM-ddTHH:mm:ssK"
    ];

    /// <summary>
    /// Maps the <c>/admin/usage/*</c> endpoints onto <paramref name="endpoints"/>, gated by the same
    /// <c>X-Admin-Token</c> header as every other <c>/admin/*</c> route.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (the proxy's inner Kestrel host).</param>
    /// <param name="reportingService">The shared reporting service backing every read.</param>
    /// <param name="managementToken">
    /// Optional shared secret; when non-empty, every <c>/admin/usage/*</c> request must present it in the
    /// <c>X-Admin-Token</c> header or receive a 401. See <see cref="ProviderAdminEndpoints.MapProviderAdminEndpoints"/>
    /// for the identical rationale.
    /// </param>
    public static IEndpointRouteBuilder MapUsageAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        ManagementReportingService reportingService,
        string? managementToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(reportingService);

        var group = endpoints.MapGroup("/admin/usage");

        if (!string.IsNullOrWhiteSpace(managementToken))
            group.AddEndpointFilter(async (context, next) =>
            {
                var provided = context.HttpContext.Request.Headers[TokenHeaderName].ToString();
                if (!ManagementAccessToken.Verify(presented: provided, expected: managementToken))
                    return Error(statusCode: StatusCodes.Status401Unauthorized,
                        message: "Missing or invalid management token.", type: "unauthorized");

                return await next(context);
            });

        // Totals for the header ticker and summary tiles - a preset window rather than an explicit range,
        // since every caller of this endpoint wants "the last day/week/month/all-time", not an arbitrary span.
        group.MapGet(pattern: "/summary", handler: (string? window) =>
            ToResult(reportingService.GetUsageSummary(window ?? "day")));

        // The Model Distribution / Cost Analytics chart feed - an explicit range, since these callers drive
        // a filter bar the operator controls directly.
        group.MapGet(pattern: "/rollup", handler: (string? from, string? to, string? width, string? groupBy) =>
        {
            if (!TryParseInstant(value: from, instant: out var fromInstant))
                return Error(statusCode: StatusCodes.Status400BadRequest,
                    message: "'from' must be a valid ISO 8601 instant.", type: "invalid_request_error");

            if (!TryParseInstant(value: to, instant: out var toInstant))
                return Error(statusCode: StatusCodes.Status400BadRequest,
                    message: "'to' must be a valid ISO 8601 instant.", type: "invalid_request_error");

            return ToResult(reportingService.GetUsageRollup(from: fromInstant, to: toInstant, width: width ?? "day",
                groupBy: groupBy ?? "day"));
        });

        // docs/router/self-organizing-classification-plan.md Phase T4: the Cost Analytics "Routing ROI"
        // feed. Polled by the GUI rather than pushed over telemetry - the savings figure depends on the
        // asynchronous comparison job, so it is deliberately not real-time and has no live event to ride.
        group.MapGet(pattern: "/routing-roi",
            handler: async (string? from, string? to, string? session, CancellationToken cancellationToken) =>
            {
                if (!TryParseInstant(value: from, instant: out var fromInstant))
                    return Error(statusCode: StatusCodes.Status400BadRequest,
                        message: "'from' must be a valid ISO 8601 instant.", type: "invalid_request_error");

                if (!TryParseInstant(value: to, instant: out var toInstant))
                    return Error(statusCode: StatusCodes.Status400BadRequest,
                        message: "'to' must be a valid ISO 8601 instant.", type: "invalid_request_error");

                return ToResult(await reportingService.GetRoutingRoiAsync(from: fromInstant, to: toInstant,
                    sessionId: session, cancellationToken: cancellationToken));
            });

        // §5.12: reuses the exact same GetUsageRollup query the chart feed above does - export is a
        // rendering choice (CSV/JSON), never a second query path - so the two can never disagree on what
        // counts as "the same range" the way an independently-written export query could.
        group.MapGet(pattern: "/export",
            handler: (string? from, string? to, string? width, string? groupBy, string? format) =>
            {
                if (!TryParseInstant(value: from, instant: out var fromInstant))
                    return Error(statusCode: StatusCodes.Status400BadRequest,
                        message: "'from' must be a valid ISO 8601 instant.", type: "invalid_request_error");

                if (!TryParseInstant(value: to, instant: out var toInstant))
                    return Error(statusCode: StatusCodes.Status400BadRequest,
                        message: "'to' must be a valid ISO 8601 instant.", type: "invalid_request_error");

                var exportFormat = format ?? "json";
                if (exportFormat is not ("csv" or "json"))
                    return Error(statusCode: StatusCodes.Status400BadRequest,
                        message: "'format' must be 'csv' or 'json'.", type: "invalid_request_error");

                var result = reportingService.GetUsageRollup(from: fromInstant, to: toInstant, width: width ?? "day",
                    groupBy: groupBy ?? "day");
                if (!result.Success) return ToResult(result);

                return exportFormat == "csv"
                    ? Results.Text(content: UsageExportFormatter.ToCsv(result.Value!), contentType: "text/csv")
                    : Results.Ok(result.Value);
            });

        return endpoints;
    }

    /// <summary>
    /// Parses <paramref name="value"/> against the exact ISO 8601 instant shapes in <see cref="IsoInstantFormats"/>,
    /// rejecting the looser shapes <see cref="DateTimeOffset.TryParse(string?, out DateTimeOffset)"/> would otherwise silently
    /// accept.
    /// </summary>
    /// <param name="value">The raw query-string value to parse.</param>
    /// <param name="instant">The parsed instant, when successful.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> matched one of the accepted formats.</returns>
    private static bool TryParseInstant(string? value, out DateTimeOffset instant)
    {
        return DateTimeOffset.TryParseExact(
            input: value,
            formats: IsoInstantFormats,
            formatProvider: CultureInfo.InvariantCulture,
            styles: DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            result: out instant);
    }

    /// <summary>
    /// Maps a facade <see cref="ManagementResult{T}"/> to an HTTP response: the value on success, or an OpenAI-shaped
    /// error whose status reflects <see cref="ManagementResult{T}.ErrorType"/> on failure.
    /// </summary>
    /// <typeparam name="T">The result's success payload type.</typeparam>
    /// <param name="result">The facade outcome to translate.</param>
    private static IResult ToResult<T>(ManagementResult<T> result)
    {
        return result.Success
            ? Results.Ok(result.Value)
            : result.ErrorType switch
            {
                ManagementErrorType.NotFound => Error(statusCode: StatusCodes.Status404NotFound,
                    message: result.ErrorMessage!, type: "not_found"),
                ManagementErrorType.InvalidRequest => Error(statusCode: StatusCodes.Status400BadRequest,
                    message: result.ErrorMessage!, type: "invalid_request_error"),
                ManagementErrorType.Unavailable => Error(statusCode: StatusCodes.Status503ServiceUnavailable,
                    message: result.ErrorMessage!, type: "unavailable"),
                _ => Error(statusCode: StatusCodes.Status500InternalServerError, message: result.ErrorMessage!,
                    type: "internal_error")
            };
    }

    /// <summary>Builds an OpenAI-shaped JSON error response with the given status code, message, and error type.</summary>
    private static IResult Error(int statusCode, string message, string type)
    {
        return Results.Json(
            data: new { error = new { message, type, code = statusCode.ToString(CultureInfo.InvariantCulture) } },
            statusCode: statusCode);
    }
}