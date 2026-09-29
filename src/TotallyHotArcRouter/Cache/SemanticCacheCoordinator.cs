using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Cache;

/// <summary>
/// The proxy's adapter for <see cref="SemanticResponseCache"/>. Looks up before the upstream call and
/// stores after a complete successful response, and writes the log lines the Console tab shows.
/// </summary>
/// <remarks>
/// A null cache, or a cache with <see cref="SemanticResponseCache.IsEnabled"/> false, returns without
/// logging, without a header, and without touching stored entries. That is the default.
/// </remarks>
internal sealed class SemanticCacheCoordinator
{
    /// <summary>
    /// Response header that reports a local semantic-cache outcome. Values are <c>hit</c> and <c>miss</c>.
    /// Absent when the cache is off or the request was not eligible. This is not a provider prompt-cache
    /// header.
    /// </summary>
    public const string HeaderName = "X-ArcRouter-Semantic-Cache";

    private static readonly object ProbeItemKey = new();

    private readonly SemanticResponseCache? _cache;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticCacheCoordinator"/> class.
    /// </summary>
    /// <param name="cache">The store, or <see langword="null"/> when the feature is not registered.</param>
    /// <param name="logger">Receives hit, miss, and store lines. Templates are static.</param>
    public SemanticCacheCoordinator(SemanticResponseCache? cache, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Serves a saved answer when one is similar enough and still valid. Returns <see langword="false"/>
    /// when the provider must be called, including when the cache is disabled.
    /// </summary>
    /// <param name="context">The client request. A hit commits its response.</param>
    /// <param name="resolution">The resolved route and the task embedding computed for routing.</param>
    /// <returns><see langword="true"/> when the client has already been answered from the cache.</returns>
    public async Task<bool> TryServeAsync(HttpContext context, ModelRouteResolutionResult resolution)
    {
        try
        {
            if (_cache is not { IsEnabled: true } || resolution.Route is null || resolution.RewrittenBody is null)
                return false;

            JsonObject? body;
            try
            {
                body = JsonNode.Parse(resolution.RewrittenBody) as JsonObject;
            }
            catch (JsonException)
            {
                return false;
            }

            if (body is null) return false;

            var route = resolution.Route;
            if (!SemanticCacheScope.TryCreateScopeKey(body: body, provider: route.Provider,
                    providerModelId: route.ProviderModelId, scopeKey: out var scopeKey,
                    refusalReason: out var refusal))
            {
                _logger.LogDebug(
                    message:
                    "Local semantic cache skipped the request ({Reason}). The provider will be called. This is TotallyHot's local semantic cache, separate from provider prompt caching.",
                    refusal);
                return false;
            }

            if (resolution.TaskEmbedding is not { Length: > 0 } embedding)
            {
                _logger.LogDebug(
                    message:
                    "Local semantic cache skipped the request (no embedding). The provider will be called. This is TotallyHot's local semantic cache, separate from provider prompt caching.");
                return false;
            }

            var probe = new SemanticCacheProbe(embedding: embedding, scopeKey: scopeKey, modelName: route.ModelName);
            var result = _cache.Lookup(probe);
            if (result.Reason == "disabled")
                return false;

            context.Items[ProbeItemKey] = probe;
            var modelName = LogRedaction.Sanitize(route.ModelName);

            if (result is { IsHit: true, Body: { } responseBody, ContentType: { } contentType } &&
                !context.Response.HasStarted)
            {
                _logger.LogInformation(
                    message:
                    "Local semantic cache hit for model {Model} (similarity {Similarity}). Reused a saved answer without calling the provider. This is TotallyHot's local semantic cache, separate from provider prompt caching.",
                    modelName,
                    result.Similarity);
                RoutingResponseHeaders.From(
                    requestedModel: resolution.RequestedModelName ?? route.ModelName,
                    routedModel: route.ModelName,
                    substitutionReason: resolution.SubstitutionReason).WriteTo(context);
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = contentType;
                context.Response.ContentLength = responseBody.Length;
                context.Response.Headers[HeaderName] = "hit";
                await context.Response.Body.WriteAsync(buffer: responseBody, cancellationToken: context.RequestAborted);
                return true;
            }

            _logger.LogInformation(
                message:
                "Local semantic cache miss for model {Model} ({Reason}). The provider will be called. This is TotallyHot's local semantic cache, separate from provider prompt caching.",
                modelName,
                result.Reason);
            if (!context.Response.HasStarted)
                context.Response.Headers[HeaderName] = "miss";

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception: ex,
                message:
                "Local semantic cache lookup failed; calling the provider instead. This is TotallyHot's local semantic cache, separate from provider prompt caching.");
            return false;
        }
    }

    /// <summary>
    /// Stores the client-facing body when this request was eligible, the response is a complete HTTP 200,
    /// and the body is not an error or a tool call. Streaming responses and disabled caches are ignored.
    /// </summary>
    /// <param name="context">The request whose lookup probe, if any, was saved by <see cref="TryServeAsync"/>.</param>
    /// <param name="statusCode">The status committed to the client.</param>
    /// <param name="responseBody">The captured client-facing body.</param>
    /// <param name="isStreaming">Whether the upstream answered with an event stream.</param>
    /// <param name="contentType">The content type to replay, or <see langword="null"/> to use JSON.</param>
    public void Remember(HttpContext context, int statusCode, byte[] responseBody, bool isStreaming, string? contentType)
    {
        try
        {
            if (_cache is not { IsEnabled: true }) return;

            if (context.Items[ProbeItemKey] is not SemanticCacheProbe probe) return;

            if (isStreaming)
            {
                LogNotStored("streaming");
                return;
            }

            if (statusCode != StatusCodes.Status200OK)
            {
                LogNotStored("error-status");
                return;
            }

            if (string.Equals(a: contentType, b: "text/event-stream", comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                LogNotStored("streaming");
                return;
            }

            if (!SemanticCacheScope.IsReusableAnswer(responseBody: responseBody, refusalReason: out var refusal))
            {
                LogNotStored(refusal);
                return;
            }

            var stored = _cache.Remember(probe: probe, statusCode: statusCode, responseBody: responseBody,
                contentType: string.IsNullOrWhiteSpace(contentType) ? "application/json" : contentType);
            if (!stored)
            {
                LogNotStored("rejected");
                return;
            }

            _logger.LogInformation(
                message:
                "Local semantic cache stored an answer for model {Model}. A later request with the same meaning can reuse it. This is TotallyHot's local semantic cache, separate from provider prompt caching.",
                LogRedaction.Sanitize(probe.ModelName));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception: ex,
                message:
                "Local semantic cache could not store the answer. The client response was unaffected. This is TotallyHot's local semantic cache, separate from provider prompt caching.");
        }
    }

    /// <summary>Debug line for an eligible request whose answer was not kept.</summary>
    private void LogNotStored(string reason)
    {
        _logger.LogDebug(
            message:
            "Local semantic cache did not store the answer ({Reason}). This is TotallyHot's local semantic cache, separate from provider prompt caching.",
            reason);
    }
}
