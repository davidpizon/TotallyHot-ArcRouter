using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Answers the three self-contained, read-only "local" endpoints <see cref="ProxyMiddleware"/> serves
/// from configuration instead of forwarding upstream: the OpenAI-compatible model list
/// (<c>GET /v1/models</c>) and Ollama's native model discovery pair (<c>GET /api/tags</c>,
/// <c>POST /api/show</c>). Extracted from <see cref="ProxyMiddleware"/> (docs/router/code-smell-
/// refactoring-plan.md Phase 2 step 1): these branches share almost no state with the forwarding path,
/// making them the safest cut of that class's ~8 mixed responsibilities.
/// </summary>
internal sealed class LocalEndpointResponder
{
    // The OpenAI-compatible model discovery path. Answered locally from configuration (mirroring LiteLLM's
    // /v1/models behavior) since it has no request body to resolve a single upstream provider from, and no
    // single upstream to forward it to anyway when ModelList spans multiple providers.
    public const string ModelsListPath = "/v1/models";

    // Ollama's native model discovery path. A client that adds this proxy as an "Ollama" provider (e.g.
    // Visual Studio's AI model picker) probes this GET endpoint - with no body - to list models, exactly
    // like ModelsListPath above but in Ollama's own response shape rather than OpenAI's. Answered the same
    // way: locally from configuration, never forwarded, since there is no body to resolve a single upstream
    // from and no single upstream anyway when ModelList spans multiple providers.
    public const string OllamaTagsPath = "/api/tags";

    // Ollama's native per-model detail path. A client that discovers models via OllamaTagsPath above (e.g.
    // Visual Studio's AI model picker) follows up with one POST here per model to fetch its details before
    // use. Answered locally from configuration, exactly like OllamaTagsPath: without this, the request falls
    // through to the normal per-model routing path, which resolves its {"model": "..."} body to a real
    // upstream candidate and forwards it there verbatim - a malformed chat/completion request that the
    // upstream (correctly) rejects, surfacing as a confusing 400 with no indication /api/show was involved.
    public const string OllamaShowPath = "/api/show";

    /// <summary>
    /// The architecture reported for the synthetic router alias, and for any model whose real architecture
    /// was never probed.
    /// </summary>
    /// <remarks>
    /// Deliberately not a real GGUF architecture name. The alias spans models of differing architectures,
    /// so any real name would be wrong for most of them; a client keying behavior off this value sees
    /// something unrecognized and falls back to generic handling, which is the safe direction. Naming a
    /// plausible-but-wrong architecture like <c>llama</c> would be strictly worse - the same judgment
    /// <c>ModelDialectResolver.TryMapArchitecture</c> already makes about that exact name.
    /// </remarks>
    private const string RouterArchitecture = "arcrouter";

    // Read only when describing models on /api/show. Both are in-memory snapshot lookups (see
    // ToolCallCapabilityStore), so consulting them from a request handler costs a dictionary probe, not a
    // query - which matters because a client's model picker polls this endpoint.
    private readonly IToolCallCapabilityStore? _capabilityStore;
    private readonly IModelContextWindowStore? _contextWindowStore;
    private readonly RequestInterceptor _interceptor;

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalEndpointResponder"/> class.
    /// </summary>
    /// <param name="logger">Logger shared with the owning <see cref="ProxyMiddleware"/> instance.</param>
    /// <param name="interceptor">Request interceptor, used only for its configured model list.</param>
    /// <param name="capabilityStore">
    /// Optional source of each model's detected tool-call dialect, used only to describe models
    /// on <c>POST /api/show</c>. <see langword="null"/> is behaviorally inert: every model reads as unclassified, which
    /// <see cref="Translation.ToolCalling.OllamaModelCapabilities.ForDialect"/> already treats as tool-capable, so the
    /// declared capabilities are unchanged.
    /// </param>
    /// <param name="contextWindowStore">
    /// Optional source of each model's probed context window, used only to populate
    /// <c>POST /api/show</c>'s <c>model_info</c>. <see langword="null"/> is behaviorally inert: <c>model_info</c> is omitted
    /// entirely.
    /// </param>
    public LocalEndpointResponder(
        ILogger logger,
        RequestInterceptor interceptor,
        IToolCallCapabilityStore? capabilityStore,
        IModelContextWindowStore? contextWindowStore)
    {
        _logger = logger;
        _interceptor = interceptor;
        _capabilityStore = capabilityStore;
        _contextWindowStore = contextWindowStore;
    }

    /// <summary>
    /// Determines whether a request targets the OpenAI-compatible model discovery endpoint
    /// (<c>GET /v1/models</c>), matched case-insensitively and with an optional trailing slash tolerated,
    /// since both conventions vary by client.
    /// </summary>
    public static bool IsModelsListRequest(HttpRequest request)
    {
        return HttpMethods.IsGet(request.Method) &&
               string.Equals(a: request.Path.Value?.TrimEnd('/'), b: ModelsListPath,
                   comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes the configured model list as an OpenAI-compatible <c>/v1/models</c> response, mirroring
    /// LiteLLM's behavior of answering this endpoint from local configuration rather than forwarding it
    /// upstream.
    /// </summary>
    public async Task WriteModelsListResponseAsync(HttpContext context)
    {
        var entries = _interceptor.ListAvailableModels()
            .Select(model => new ModelListEntry(Id: model.ModelName, Object: "model", 0, OwnedBy: model.Provider))
            .ToList();

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(
            text: JsonSerializer.Serialize(new ModelsListResponse(Object: "list", Data: entries)),
            cancellationToken: context.RequestAborted);
    }

    /// <summary>
    /// Determines whether a request targets Ollama's native model discovery endpoint
    /// (<c>GET /api/tags</c>), matched case-insensitively and with an optional trailing slash tolerated,
    /// mirroring <see cref="IsModelsListRequest"/>.
    /// </summary>
    public static bool IsOllamaTagsRequest(HttpRequest request)
    {
        return HttpMethods.IsGet(request.Method) &&
               string.Equals(a: request.Path.Value?.TrimEnd('/'), b: OllamaTagsPath,
                   comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes the configured model list as an Ollama-native <c>/api/tags</c> response, so a client that
    /// added this proxy as an "Ollama" provider (e.g. Visual Studio's AI model picker) can discover the
    /// configured models the same way <see cref="WriteModelsListResponseAsync"/> answers the OpenAI-shaped
    /// discovery endpoint.
    /// </summary>
    public async Task WriteOllamaTagsResponseAsync(HttpContext context)
    {
        var entries = _interceptor.ListAvailableModels()
            .Select(model => new OllamaTagEntry(
                Name: model.ModelName,
                Model: model.ModelName,
                ModifiedAt: DateTimeOffset.UtcNow.ToString("O"),
                0,
                Digest: string.Empty,
                Details: new OllamaTagDetails(Format: "gguf", Family: string.Empty, ParameterSize: string.Empty)))
            .ToList();

        // Debug, not Information: this fires on every poll from an Ollama-shaped client's model picker
        // (potentially frequent), and its outcome is fully captured by the response itself - this exists so
        // a trace can distinguish "answered locally from /api/tags" from the per-model routing path's own
        // logging, without adding noise at the default log level.
        _logger.LogDebug(message: "Answered {Path} locally with {Count} configured model(s).", OllamaTagsPath,
            entries.Count);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(
            text: JsonSerializer.Serialize(new OllamaTagsResponse(entries)),
            cancellationToken: context.RequestAborted);
    }

    /// <summary>
    /// Determines whether a request targets Ollama's native per-model detail endpoint (<c>POST /api/show</c>),
    /// matched case-insensitively and with an optional trailing slash tolerated, mirroring
    /// <see cref="IsOllamaTagsRequest"/>.
    /// </summary>
    public static bool IsOllamaShowRequest(HttpRequest request)
    {
        return HttpMethods.IsPost(request.Method) &&
               string.Equals(a: request.Path.Value?.TrimEnd('/'), b: OllamaShowPath,
                   comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Answers Ollama's native <c>POST /api/show</c> from local configuration instead of forwarding it
    /// upstream - see <see cref="OllamaShowPath"/>'s remarks for why forwarding it produces a confusing 400.
    /// Reads the requested model name out of the body's <c>{"model": "..."}</c> field the same way
    /// <see cref="RequestInterceptor.ResolveModelRouteAsync"/> does, and answers 404 for a model this proxy
    /// does not have configured - matching real Ollama's own behavior for an unknown model, which
    /// <see cref="Translation.ToolCalling.ModelDialectResolver"/>'s own Ollama probe already relies on when
    /// probing a genuine Ollama endpoint.
    /// </summary>
    public async Task WriteOllamaShowResponseAsync(HttpContext context)
    {
        string body;
        using (var reader = new StreamReader(stream: context.Request.Body, encoding: Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        string? modelName = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject jsonObject &&
                jsonObject["model"] is JsonValue modelValue &&
                modelValue.TryGetValue<string>(out var value))
                modelName = value;
        }
        catch (JsonException)
        {
            // Falls through to the "model not found" response below, matching real Ollama's own behavior
            // for a request it cannot make sense of.
        }

        var model = modelName is null
            ? null
            : _interceptor.ListAvailableModels()
                .FirstOrDefault(m =>
                    string.Equals(a: m.ModelName, b: modelName, comparisonType: StringComparison.OrdinalIgnoreCase));

        if (model is null)
        {
            // Information, not Debug: unlike the tags poll above, this is the direct diagnostic signal for
            // exactly the failure mode this endpoint exists to prevent - a client naming a model this proxy
            // doesn't know, which without this local answer would instead fall through to the per-model
            // routing path and surface as a confusing 400 from whatever upstream that name happened to
            // resolve to (see OllamaShowPath's remarks).
            _logger.LogInformation(
                message: "Answered {Path} locally: unknown model '{ModelName}' requested; returning 404.",
                OllamaShowPath,
                LogRedaction.Sanitize(modelName));
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                text: JsonSerializer.Serialize(new OllamaErrorResponse($"model '{modelName}' not found")),
                cancellationToken: context.RequestAborted);
            return;
        }

        _logger.LogDebug(message: "Answered {Path} locally for model {Model}.", OllamaShowPath,
            LogRedaction.Sanitize(model.ModelName));

        var (capabilities, modelInfo) = DescribeModel(model);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(
            text: JsonSerializer.Serialize(new OllamaShowResponse(
                Modelfile: string.Empty,
                Parameters: string.Empty,
                Template: string.Empty,
                Details: new OllamaTagDetails(Format: "gguf", Family: string.Empty, ParameterSize: string.Empty),
                ModelInfo: modelInfo,
                Capabilities: capabilities)),
            cancellationToken: context.RequestAborted);
    }

    /// <summary>
    /// Describes one model the way Ollama's <c>/api/show</c> does: what it can do, and how much context it
    /// accepts. Both are read from in-memory snapshots, never probed here - see
    /// <see cref="Translation.ToolCalling.IModelContextWindowStore"/> for why an inline probe would be
    /// wrong on a request path a model picker polls.
    /// </summary>
    /// <remarks>
    /// The synthetic router alias is answered by aggregating over the models it could actually dispatch to:
    /// the union of their capabilities and the maximum of their context windows, restricted to models that
    /// pass both governance gates. Note <see cref="RequestInterceptor.ListAvailableModels"/> does no
    /// enablement filtering of its own, so that restriction is applied here.
    /// <para>
    /// The alias is identified by provider key rather than by name. An operator can legitimately configure
    /// a model called <c>totallyhot-arcrouter</c>, but the provider key
    /// <see cref="RequestInterceptor.RouterModelProvider"/> is documented as deliberately not a real
    /// provider, so it is unambiguous.
    /// </para>
    /// <para>
    /// This is the same in-memory join <c>ManagementFacade</c> already performs to populate its per-model
    /// admin view.
    /// </para>
    /// </remarks>
    /// <param name="model">The model being described.</param>
    /// <returns>
    /// The capability tokens (never empty), and the <c>model_info</c> map - or <see langword="null"/> when
    /// no context length is known, so the field is omitted rather than reported as zero.
    /// </returns>
    private (IReadOnlyList<string> Capabilities, IReadOnlyDictionary<string, JsonNode>? ModelInfo) DescribeModel(
        AvailableModel model)
    {
        if (string.Equals(a: model.Provider, b: RequestInterceptor.RouterModelProvider,
                comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            var eligible = _interceptor.ListAvailableModels()
                .Where(m => !string.Equals(a: m.Provider, b: RequestInterceptor.RouterModelProvider,
                    comparisonType: StringComparison.OrdinalIgnoreCase))
                .Where(m => _interceptor.IsProviderEnabled(m.Provider) && _interceptor.IsModelEnabled(m.ModelName))
                .ToList();

            var union = OllamaModelCapabilities.Union(
                eligible.Select(m => OllamaModelCapabilities.ForDialect(
                    _capabilityStore?.GetModelCapability(providerKey: m.Provider, modelName: m.ModelName)?.Dialect)));

            // Max, not min: the alias advertises the best it can route to. This over-advertises by
            // construction - auto-select may land on a model with a smaller window - which is the accepted
            // trade-off recorded in docs/router/ollama-show-capabilities-plan.md.
            var widest = eligible
                .Select(m =>
                    _contextWindowStore?.GetModelContextWindow(providerKey: m.Provider, modelName: m.ModelName)
                        ?.ContextLength)
                .Where(length => length is > 0)
                .DefaultIfEmpty(null)
                .Max();

            return (union, BuildModelInfo(architecture: RouterArchitecture, contextLength: widest));
        }

        var capabilities = OllamaModelCapabilities.ForDialect(
            _capabilityStore?.GetModelCapability(providerKey: model.Provider, modelName: model.ModelName)?.Dialect);
        var window =
            _contextWindowStore?.GetModelContextWindow(providerKey: model.Provider, modelName: model.ModelName);

        return (capabilities,
            BuildModelInfo(architecture: window?.Architecture ?? RouterArchitecture,
                contextLength: window?.ContextLength));
    }

    /// <summary>
    /// Builds Ollama's <c>model_info</c> map, or <see langword="null"/> when no context length is known.
    /// </summary>
    /// <remarks>
    /// The architecture and the length are always emitted together. Ollama keys the window as
    /// <c>{arch}.context_length</c> and clients resolve it by reading <c>general.architecture</c> first, so
    /// a length published without a matching architecture is unreachable through the standard read path.
    /// </remarks>
    private static IReadOnlyDictionary<string, JsonNode>? BuildModelInfo(string architecture, int? contextLength)
    {
        return contextLength is > 0
            ? new Dictionary<string, JsonNode>(StringComparer.Ordinal)
            {
                ["general.architecture"] = JsonValue.Create(architecture),
                [$"{architecture}.context_length"] = JsonValue.Create(contextLength.Value)
            }
            : null;
    }

    /// <summary>
    /// A single entry in the <c>/v1/models</c> response, shaped to match OpenAI's model list schema.
    /// </summary>
    private sealed record ModelListEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("created")]
        long Created,
        [property: JsonPropertyName("owned_by")]
        string OwnedBy);

    /// <summary>
    /// The top-level <c>/v1/models</c> response envelope, shaped to match OpenAI's model list schema.
    /// </summary>
    private sealed record ModelsListResponse(
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("data")] IReadOnlyList<ModelListEntry> Data);

    /// <summary>
    /// The <c>details</c> object of one <see cref="OllamaTagEntry"/>, shaped to match Ollama's
    /// <c>/api/tags</c> schema. Only the fields Ollama always populates are set; format-specific fields the
    /// router has no equivalent for are left as ordinary defaults rather than fabricated.
    /// </summary>
    private sealed record OllamaTagDetails(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("family")] string Family,
        [property: JsonPropertyName("parameter_size")]
        string ParameterSize);

    /// <summary>
    /// A single entry in the <c>/api/tags</c> response, shaped to match Ollama's native model list schema.
    /// </summary>
    private sealed record OllamaTagEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("modified_at")]
        string ModifiedAt,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("details")]
        OllamaTagDetails Details);

    /// <summary>
    /// The top-level <c>/api/tags</c> response envelope, shaped to match Ollama's native model list schema.
    /// </summary>
    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<OllamaTagEntry> Models);

    /// <summary>
    /// The <c>POST /api/show</c> response envelope, shaped to match Ollama's native per-model detail schema.
    /// Only the fields every Ollama install always populates are set; format-specific fields the router has
    /// no equivalent for (the model's literal chat template among them - see
    /// <see cref="Translation.ToolCalling.ModelDialectResolver"/> for where that is actually sourced from,
    /// when it is available at all) are left as ordinary defaults rather than fabricated.
    /// </summary>
    /// <remarks>
    /// <c>capabilities</c> is the exception to that "leave it blank" stance, and the reason this endpoint
    /// gained content at all: capability-filtering clients drop a model that declares nothing, so leaving
    /// it empty made every router model invisible in Visual Studio's Copilot picker. It is the one field
    /// here the router genuinely knows the answer to.
    /// </remarks>
    private sealed record OllamaShowResponse(
        [property: JsonPropertyName("modelfile")]
        string Modelfile,
        [property: JsonPropertyName("parameters")]
        string Parameters,
        [property: JsonPropertyName("template")]
        string Template,
        [property: JsonPropertyName("details")]
        OllamaTagDetails Details,

        // Omitted rather than serialized as null when unknown. This endpoint serializes without options, so
        // default handling would write `"model_info": null` - which a client can read as "no context
        // limit" rather than "not stated". The per-property attribute is what keeps that promise without
        // introducing a shared options object for one field.
        [property: JsonPropertyName("model_info")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, JsonNode>? ModelInfo,
        [property: JsonPropertyName("capabilities")]
        IReadOnlyList<string> Capabilities);

    /// <summary>An Ollama-shaped <c>{"error": "..."}</c> envelope, used for a <c>POST /api/show</c> naming an unknown model.</summary>
    private sealed record OllamaErrorResponse(
        [property: JsonPropertyName("error")] string Error);
}