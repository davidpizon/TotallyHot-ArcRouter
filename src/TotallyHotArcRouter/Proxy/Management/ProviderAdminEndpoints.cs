namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Maps the localhost management REST API the Governance UI uses to add/remove/edit provider
/// endpoints and credentials and list/discover each provider's models. All logic - projection, merging,
/// credential/header masking, and validation - lives in <see cref="ManagementFacade"/>, the same facade
/// the MCP endpoint's provider tools call, so both surfaces share one behavior. This file only translates
/// HTTP requests into facade calls and <see cref="ManagementResult{T}"/> outcomes into HTTP responses.
/// Writes go through <see cref="IProviderConfigStore"/> (via the facade), so edits are validated,
/// persisted, and live-reloaded into the running router without a restart. These endpoints share the
/// plain-HTTP loopback proxy port; when a management token is configured they additionally require a
/// matching <c>X-Admin-Token</c> header.
/// </summary>
public static class ProviderAdminEndpoints
{
    private const string TokenHeaderName = "X-Admin-Token";

    /// <summary>
    /// Maps the <c>/admin/*</c> provider-management endpoints onto <paramref name="endpoints"/>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (the proxy's inner Kestrel host).</param>
    /// <param name="facade">The shared management facade backing every read/write.</param>
    /// <param name="managementToken">
    /// Optional shared secret; when non-empty, every <c>/admin/*</c> request must present it in the
    /// <c>X-Admin-Token</c> header or receive a 401. In production this is the always-present
    /// <see cref="ManagementAccessToken"/> value, so the API is gated by default; a caller that wants an
    /// ungated surface (e.g. a test exercising forwarding only) passes <see langword="null"/>.
    /// </param>
    public static IEndpointRouteBuilder MapProviderAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        ManagementFacade facade,
        string? managementToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(facade);

        var group = endpoints.MapGroup("/admin");

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

        group.MapGet("/providers", () => Results.Ok(facade.ListProviders()));

        group.MapPut("/providers/{key}", async (string key, ProviderWriteRequest request, CancellationToken cancellationToken) =>
            ToResult(await facade.UpsertProviderAsync(key, request, cancellationToken)));

        group.MapDelete("/providers/{key}", async (string key, CancellationToken cancellationToken) =>
            ToResult(await facade.RemoveProviderAsync(key, cancellationToken)));

        // Sets or clears a provider's monthly budget caps (Governance > Providers). A null cap clears that
        // dimension; both null removes the budget entirely. Persisted to the local database via the budget
        // store, so the cap survives a restart and is enforced live by ProxyMiddleware. Returns the refreshed
        // provider list (now carrying the new caps and current-month spend) like the other mutations.
        group.MapPut("/providers/{key}/budget", (string key, ProviderBudgetWriteRequest request) =>
            ToResult(facade.SetBudget(key, request)));

        // Switches a provider on or off (the Stop/Play control in Governance > Providers). A dedicated route
        // rather than a field on PUT /providers/{key}: that path rebuilds the provider from the write request
        // and would drop a Bedrock provider's Aws* fields. Enforced immediately on the next request - see
        // ProviderOptions.Enabled. Returns the refreshed provider list like the other mutations.
        group.MapPut("/providers/{key}/enabled", async (string key, ProviderEnabledWriteRequest request, CancellationToken cancellationToken) =>
            ToResult(await facade.SetEnabledAsync(key, request, cancellationToken)));

        group.MapPut("/providers/{key}/models/{modelName}", async (string key, string modelName, ModelWriteRequest request, CancellationToken cancellationToken) =>
            ToResult(await facade.UpsertModelAsync(key, modelName, request, cancellationToken)));

        group.MapDelete("/providers/{key}/models/{modelName}", async (string key, string modelName, CancellationToken cancellationToken) =>
            ToResult(await facade.RemoveModelAsync(modelName, cancellationToken)));

        // Switches a model on or off (the per-model Start/Stop control in Governance > Providers) - the
        // model-level twin of PUT /providers/{key}/enabled, same dedicated-route rationale.
        group.MapPut("/providers/{key}/models/{modelName}/enabled", async (string key, string modelName, ModelEnabledWriteRequest request, CancellationToken cancellationToken) =>
            ToResult(await facade.SetModelEnabledAsync(modelName, request, cancellationToken)));

        // Pins how a model expresses tool calls, overriding automatic detection - the equivalent of
        // LiteLLM's register_model(..., supports_function_calling=…). A null/empty dialect clears the pin.
        // Synchronous, unlike its neighbours: it writes one already-known row rather than probing anything.
        group.MapPut("/providers/{key}/models/{modelName}/tool-dialect", (string key, string modelName, ModelToolDialectWriteRequest request) =>
            ToResult(facade.SetModelToolDialect(key, modelName, request)));

        group.MapPost("/providers/{key}/discover-models", async (string key, CancellationToken cancellationToken) =>
        {
            var result = await facade.DiscoverModelsAsync(key, cancellationToken);
            return ToResult(result);
        });

        // Re-probes which API flavors the provider's endpoint answers (see
        // docs/router/tool-call-normalization.md §3.3). The same scan runs automatically after a provider
        // save; this exists for the cases that misses - a local server that was down at the time, or one
        // that has since gained a native API. Kept as an independently callable building block alongside the
        // consolidated refresh-from-endpoint route below.
        group.MapPost("/providers/{key}/scan-capabilities", async (string key, CancellationToken cancellationToken) =>
        {
            var result = await facade.ScanCapabilitiesAsync(key, cancellationToken);
            return ToResult(result);
        });

        // The Governance UI's "Refresh from endpoint" action: discovers the provider's live model list,
        // reconciles it into configuration (adding new models as stopped, flagging missing ones absent -
        // never deleting), then re-scans endpoint flavors and re-runs dialect detection - one request instead
        // of the GUI orchestrating discover-models + scan-capabilities itself.
        group.MapPost("/providers/{key}/refresh-from-endpoint", async (string key, CancellationToken cancellationToken) =>
        {
            var result = await facade.RefreshFromEndpointAsync(key, cancellationToken);
            return ToResult(result);
        });

        // The §5.7 resolution ladder's operator-override rung (docs/router/token-tracking-implementation-plan.md
        // Phase 3): runtime-editable, no restart required. GET lists every configured override for the
        // Governance price-overrides pane's read-only diagnosis view; PUT adds/replaces one; DELETE removes one.
        group.MapGet("/price-overrides", () => ToResult(facade.ListPriceOverrides()));

        // The pane's read-only diagnosis view: per configured model, whether a price resolves today and
        // via an exact or approximate match - what tells an operator which models actually need an override.
        group.MapGet("/price-resolution", () => ToResult(facade.GetPriceResolutionDiagnosis()));

        group.MapPut("/price-overrides", (PriceOverrideWriteRequest request) =>
            ToResult(facade.SetPriceOverride(request)));

        group.MapDelete("/price-overrides", (string sourceName, string aggregatorModelKey) =>
            ToResult(facade.RemovePriceOverride(sourceName, aggregatorModelKey)));

        // The Providers card's rate-limit trend chart data source (§5.9): per-dimension remaining-over-time
        // history for the last `hours` hours (default 6). Read-only, so a plain GET like every other query
        // route in this file.
        group.MapGet("/providers/{key}/rate-limit-history", (string key, double? hours) =>
            ToResult(facade.GetRateLimitHistory(key, hours ?? 6.0)));

        // Write-only protected-secret surface (docs/router/secrets-at-rest-plan.md §4/§7): stores or clears
        // a reconciliation Admin API key. Deliberately no GET counterpart - the invariant is that no secret
        // that reaches the protected store is ever readable back through any management API. GET
        // /admin/providers reports only the HasStoredAdminKey boolean.
        group.MapPut("/secrets/{name}", (string name, SecretWriteRequest request) =>
            ToResult(facade.SetSecret(name, request.Value)));

        group.MapDelete("/secrets/{name}", (string name) =>
            ToResult(facade.DeleteSecret(name)));

        return endpoints;
    }

    /// <summary>Maps a facade <see cref="ManagementResult{T}"/> to an HTTP response: the value on success, or an OpenAI-shaped error whose status reflects <see cref="ManagementResult{T}.ErrorType"/> on failure.</summary>
    /// <typeparam name="T">The result's success payload type.</typeparam>
    /// <param name="result">The facade outcome to translate.</param>
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
            new { error = new { message, type, code = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
            statusCode: statusCode);
}

