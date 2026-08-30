using System.Net.Http.Json;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Resolves how one model expresses a tool call, using only free metadata sources - detection tiers 1
/// through 3 of <c>docs/router/tool-call-normalization.md</c> §3.2.
///
/// <para>
/// Tier 1 reads the model's literal chat template from Ollama's <c>/api/show</c>; tier 2 reads the model's
/// architecture from LM Studio's native model list; tier 3 guesses from the model id. Each tier is tried
/// only when the one above it produced nothing, and each records a <see cref="DetectionConfidence"/> that
/// says how it was learned - so tier 4's live observation (Phase 4) can correct any of them, and an
/// operator pin can never be overwritten by any of them.
/// </para>
///
/// <para>
/// <b>A model whose dialect is unresolved writes no dialect row at all.</b> That is the deliberate design,
/// not a gap: a missing row means "forward natively and classify from the first real response", which is
/// both correct and free, whereas a wrong row means arming the wrong scanner against every response the
/// model produces. Silence is strictly better than a guess here.
/// </para>
///
/// <para>
/// The same two probes also yield each model's <b>context window</b>, which is reported to Ollama-shaped
/// clients through <c>POST /api/show</c> (<c>docs/router/ollama-show-capabilities-plan.md</c>). It rides
/// along at no cost - Ollama's <c>/api/show</c> and LM Studio's model list already carry it in the very
/// documents tiers 1 and 2 fetch to read a template and an architecture. It is reported <em>independently</em>
/// of the dialect: the two travel together in <see cref="ModelMetadataProbeResult"/> precisely because
/// several paths that learn no dialect still read a perfectly good window, and the previous shape - a bare
/// nullable capability - discarded the whole probe on every one of them.
/// </para>
/// </summary>
/// <remarks>
/// Every probe is best-effort and this type never throws for a runtime condition - an unreachable server, a
/// model the native API has never heard of, or a body in an unexpected shape all resolve to
/// <see langword="null"/>. Callers run this after a save has already succeeded, so a detection failure must
/// never surface as a failed operation.
/// </remarks>
public sealed class ModelDialectResolver
{
    // Ollama's /api/show returns the model's full Modelfile alongside its template, and a large model's
    // license text alone can run to tens of kilobytes. Only the template is read, but the whole body is
    // buffered to parse it, so this caps what one probe can pull into memory. Generous next to any real
    // chat template (the largest built-in Ollama templates are a few kilobytes) and small next to the
    // license blocks that dominate the payload.
    private const int MaxShowResponseBytes = 512 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IEnvironmentVariableProvider _environment;

    /// <summary>Initializes a new instance of the <see cref="ModelDialectResolver"/> class.</summary>
    /// <param name="httpClient">Client used to issue the metadata probes.</param>
    /// <param name="environment">Accessor used to resolve provider credentials and header env vars.</param>
    public ModelDialectResolver(HttpClient httpClient, IEnvironmentVariableProvider environment)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(environment);

        _httpClient = httpClient;
        _environment = environment;
    }

    /// <summary>
    /// Resolves <paramref name="modelName"/>'s tool-call dialect from the cheapest source that can answer.
    /// </summary>
    /// <param name="providerKey">The <c>ModelRouting:Providers</c> key serving this model.</param>
    /// <param name="provider">The provider's connection details, used to reach its native APIs.</param>
    /// <param name="endpointCapabilities">
    /// What <see cref="ProviderEndpointScanner"/> found this provider answers, or <see langword="null"/>
    /// when it has never been scanned. Gates tiers 1 and 2: probing an Ollama path on a provider known not
    /// to speak Ollama is a guaranteed 404, so the flags turn two wasted round trips into none.
    /// </param>
    /// <param name="modelName">The client-facing model name - the key the result is recorded under.</param>
    /// <param name="providerModelId">
    /// The upstream model identifier. Distinct from <paramref name="modelName"/> on purpose: this is the id
    /// the provider's own API knows the model by, so it is what the native probes must ask about, and it is
    /// also the more informative of the two for tier 3 - an operator's alias can be anything, but the
    /// upstream id names the real model.
    /// </param>
    /// <param name="cancellationToken">Cancels the probes.</param>
    /// <returns>
    /// Always non-null, but with either member <see langword="null"/> independently:
    /// <see cref="ModelMetadataProbeResult.Capability"/> is <see langword="null"/> when no tier could
    /// classify the dialect (see the type-level note on why that is recorded as nothing rather than as a
    /// guess), and <see cref="ModelMetadataProbeResult.ContextWindow"/> is <see langword="null"/> when no
    /// probe reported a usable context length - which is the norm for every provider that answers neither
    /// Ollama nor LM Studio natively.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="providerKey"/> or <paramref name="modelName"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public async Task<ModelMetadataProbeResult> ResolveAsync(
        string providerKey,
        ProviderOptions provider,
        ProviderEndpointCapabilities? endpointCapabilities,
        string modelName,
        string providerModelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var upstreamId = string.IsNullOrWhiteSpace(providerModelId) ? modelName : providerModelId;

        // Accumulated across the tiers rather than returned from whichever one happens to fire, because the
        // dialect and the context window are learned independently: several paths below classify no dialect
        // at all yet have already read a perfectly good window out of the same response. Assigned with ??=
        // so tier 1 (Ollama, which reads the model's own metadata) wins over tier 2 if both somehow run.
        ModelContextWindow? window = null;

        // Tier 1 - the literal chat template. The template is not evidence *about* the model's reply syntax,
        // it is the thing that decides it, which is why nothing short of a live observation outranks it.
        if (endpointCapabilities?.OllamaNative == true)
        {
            var show = await TryReadOllamaShowAsync(provider, upstreamId, cancellationToken).ConfigureAwait(false);
            window ??= BuildWindow(providerKey, modelName, show.Architecture, show.ContextLength,
                "Ollama /api/show model_info.");

            if (show.Template is not null)
            {
                var matched = MatchTemplate(show.Template);

                // A template that was read successfully and matched nothing is a *conclusive negative*, so
                // this returns instead of falling through. The lower tiers only ever guess from a name, and
                // the ground truth we just read has already contradicted every dialect they could propose -
                // preferring a filename guess over that would be strictly worse than recording nothing.
                if (matched is not null)
                {
                    return new(Capability(providerKey, modelName, matched.Value.Dialect, DetectionConfidence.Template,
                        $"Ollama /api/show template contains '{matched.Value.Delimiter}'."), window);
                }

                // Matched nothing *and* renders no tools at all: this model cannot express a tool call in
                // any syntax, so emulation is the only way it ever calls one (Phase 5). Recording it here
                // is the selection signal Phase 4 deliberately did without - and unlike the unmatched-
                // response counter Phase 4 rejected, it is not an inference from behavior: the template is
                // the mechanism that renders a tool call, read directly, so its silence is mechanical
                // rather than circumstantial.
                //
                // Note the RendersTools branch returns a null *capability* while still carrying `window`.
                // That path previously discarded the entire probe; the model whose template renders tools
                // in a dialect this build does not know is exactly the one whose window is most reliably
                // readable, so throwing it away was pure loss.
                return RendersTools(show.Template)
                    ? new(null, window)
                    : new(Capability(providerKey, modelName, ToolCallDialectRegistry.Emulated, DetectionConfidence.Template,
                        "Ollama /api/show template renders no tools."), window);
            }
        }

        // Tier 2 - the architecture recorded in the model file's own metadata.
        if (endpointCapabilities?.LmStudioNative == true)
        {
            var lmStudio = await TryReadLmStudioModelAsync(provider, upstreamId, cancellationToken).ConfigureAwait(false);
            window ??= BuildWindow(providerKey, modelName, lmStudio.Architecture, lmStudio.ContextLength,
                "LM Studio /api/v0/models.");

            if (lmStudio.Architecture is not null && TryMapArchitecture(lmStudio.Architecture, out var archDialect))
            {
                return new(Capability(providerKey, modelName, archDialect, DetectionConfidence.Template,
                    $"LM Studio reports architecture '{lmStudio.Architecture}'."), window);
            }

            // An architecture that mapped to nothing falls through rather than returning, unlike tier 1's
            // miss. The two are not comparable: tier 1 read the template itself, while an unmapped
            // architecture usually means an architecture too generic to imply a template (see
            // TryMapArchitecture) - which leaves the model id genuinely more informative, not less.
            // The window read above survives the fall-through; only the dialect question moves on.
        }

        // Tier 3 - the model id. Free, instant, and defeated by any rename, hence Heuristic.
        var heuristic = MatchModelId(upstreamId) ?? MatchModelId(modelName);
        return new(
            heuristic is null
                ? null
                : Capability(providerKey, modelName, heuristic.Value.Dialect, DetectionConfidence.Heuristic,
                    $"Model id contains '{heuristic.Value.Token}'."),
            window);
    }

    /// <summary>
    /// Builds a context-window row, or <see langword="null"/> when the probe reported no usable length.
    /// </summary>
    /// <remarks>
    /// The single place "absent, not zero" is decided. A provider that answered without a context length,
    /// or with a non-positive one, must produce no row at all - the store's write path is unconditional, so
    /// a zero written here would overwrite a good value read on an earlier scan, and
    /// <c>/api/show</c> would then advertise a limit of nothing.
    /// </remarks>
    private static ModelContextWindow? BuildWindow(
        string providerKey, string modelName, string? architecture, int? contextLength, string evidence) =>
        contextLength is > 0
            ? new ModelContextWindow(providerKey, modelName, contextLength.Value, architecture, evidence, DateTimeOffset.UtcNow)
            : null;

    /// <summary>Builds a capability row, stamped now and carrying the dialect's persisted name.</summary>
    private static ModelToolCapability Capability(
        string providerKey, string modelName, ToolCallDialect dialect, DetectionConfidence confidence, string evidence) =>
        new(providerKey, modelName, dialect.Name, confidence, evidence, ObservationCount: 0, DateTimeOffset.UtcNow);

    /// <summary>
    /// Finds the first registered dialect whose opening delimiter appears in a chat template.
    /// </summary>
    /// <remarks>
    /// Deliberately matched against <see cref="ToolCallDialectRegistry.ScannableDialects"/> rather than a
    /// separate table of detection regexes. One table means adding a dialect entry buys both detection and
    /// normalization at once, and - more importantly - makes it impossible for the two to drift into
    /// disagreeing about what a dialect looks like, which would show up as a model detected correctly and
    /// then scanned with delimiters that never match.
    /// </remarks>
    private static (ToolCallDialect Dialect, string Delimiter)? MatchTemplate(string template)
    {
        foreach (var dialect in ToolCallDialectRegistry.ScannableDialects)
        {
            foreach (var delimiter in dialect.Delimiters)
            {
                if (template.Contains(delimiter.Open, StringComparison.OrdinalIgnoreCase))
                {
                    return (dialect, delimiter.Open);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an Ollama chat template renders tools at all, independently of which dialect it uses.
    /// </summary>
    /// <remarks>
    /// This is the guard that keeps <see cref="MatchTemplate"/>'s miss from being read as "has no tool
    /// calling". The two are genuinely different: a template can support tools perfectly well in a dialect
    /// this build has not registered - DeepSeek is the known live example, and
    /// <see cref="ToolCallDialectRegistry"/> documents at length why guessing its delimiters would be worse
    /// than leaving it out. Condemning such a model to emulation would strip the native tool support it
    /// actually has.
    /// <para>
    /// Ollama templates are Go text templates, and the tool-bearing ones reference <c>.Tools</c> to render
    /// the schemas and <c>.ToolCalls</c> to render the assistant's calls back into the transcript. A
    /// template mentioning neither has no path by which a tool schema could reach the model, whatever the
    /// underlying weights were trained on - which is exactly the condition emulation answers. Matched as
    /// plain substrings rather than parsed: the question is only whether the identifier appears anywhere,
    /// and a Go template parser to answer a yes/no would be a large dependency for no extra accuracy.
    /// </para>
    /// </remarks>
    private static bool RendersTools(string template) =>
        template.Contains(".Tools", StringComparison.OrdinalIgnoreCase)
        || template.Contains(".ToolCalls", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps an LM Studio architecture to the dialect its family's chat template uses, for the
    /// architectures that actually imply one.
    /// </summary>
    /// <remarks>
    /// Conservative on purpose, and the omissions matter more than the entries. <c>llama</c> is the obvious
    /// candidate and is deliberately absent: it is the architecture reported by Llama 2, Llama 3, and the
    /// enormous population of fine-tunes built on them - including Hermes, whose template is not Llama's at
    /// all. Mapping it would produce a <see cref="DetectionConfidence.Template"/> row that is wrong for a
    /// large fraction of the models carrying it, and a wrong high-confidence row is worse than none, since
    /// it also outranks the tier-3 guess that would have read <c>hermes</c> straight out of the model id.
    /// </remarks>
    private static bool TryMapArchitecture(string architecture, out ToolCallDialect dialect)
    {
        // Prefix-matched because the architecture carries a generation (qwen2, qwen3, ...) and every
        // generation of these two families has kept its template's tool-call framing.
        if (architecture.StartsWith("qwen", StringComparison.OrdinalIgnoreCase))
        {
            dialect = ToolCallDialectRegistry.Hermes;
            return true;
        }

        if (architecture.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("mixtral", StringComparison.OrdinalIgnoreCase))
        {
            dialect = ToolCallDialectRegistry.Mistral;
            return true;
        }

        dialect = ToolCallDialectRegistry.OpenAiNative;
        return false;
    }

    /// <summary>
    /// The tier-3 model-id table, in match order.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing. <c>hermes</c> precedes <c>llama-3</c> because a Hermes release is normally
    /// named for the base it fine-tuned - <c>hermes-3-llama-3.1-8b</c> matches both tokens, and the Hermes
    /// template is the one it actually ships with. Matching on the base would get the well-known case
    /// exactly backwards.
    /// </remarks>
    private static readonly (string Token, ToolCallDialect Dialect)[] ModelIdHeuristics =
    [
        ("hermes", ToolCallDialectRegistry.Hermes),
        ("qwen", ToolCallDialectRegistry.Hermes),
        ("mixtral", ToolCallDialectRegistry.Mistral),
        ("mistral", ToolCallDialectRegistry.Mistral),

        // Both spellings, because the two are used interchangeably in published model ids
        // (meta-llama/Llama-3.1-8B-Instruct vs. llama3.1:8b). Llama 2 is absent: it has no tool-call
        // template, so a match would arm a scanner against a model that can never satisfy it.
        ("llama-3", ToolCallDialectRegistry.Llama3Json),
        ("llama3", ToolCallDialectRegistry.Llama3Json),
    ];

    /// <summary>Finds the first heuristic token present in a model id.</summary>
    private static (string Token, ToolCallDialect Dialect)? MatchModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        foreach (var (token, dialect) in ModelIdHeuristics)
        {
            if (modelId.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return (token, dialect);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a model's literal chat template from Ollama's <c>POST /api/show</c>, or returns
    /// <see langword="null"/> when the endpoint could not answer for it.
    /// </summary>
    /// <remarks>
    /// The <see langword="null"/> return means "no template was read" and is distinct from an empty match:
    /// callers fall through to a lower tier on <see langword="null"/>, but treat a template that was read
    /// and matched nothing as conclusive.
    /// </remarks>
    private async Task<OllamaShowMetadata> TryReadOllamaShowAsync(
        ProviderOptions provider, string modelId, CancellationToken cancellationToken)
    {
        var root = ProviderUrlBuilder.StripVersionSuffix(provider.BaseUrl);
        if (!Uri.TryCreate($"{root}/api/show", UriKind.Absolute, out var target))
        {
            return default;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = JsonContent.Create(new { model = modelId }),
            };
            ProviderCredentialResolver.ApplyToRequest(request, provider, _environment);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            // Ollama answers 404 for a model it does not have, so reaching here means this model exists and
            // the body is its metadata.
            var body = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return default;
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var template = document.RootElement.TryGetProperty("template", out var templateElement)
                && templateElement.ValueKind == JsonValueKind.String
                ? templateElement.GetString()
                : null;

            // Read independently of the template rather than after it. A body carrying model_info but no
            // usable template is a real shape (an embedding model, a Modelfile with no template set), and
            // returning early on the missing template used to throw away the whole parsed document.
            var (architecture, contextLength) = ReadOllamaModelInfo(document.RootElement);

            return new OllamaShowMetadata(template, architecture, contextLength);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Extracts the architecture and context length from an Ollama <c>/api/show</c> body's
    /// <c>model_info</c> object.
    /// </summary>
    /// <remarks>
    /// Ollama keys the window by architecture - <c>{arch}.context_length</c> - and names the architecture
    /// separately under <c>general.architecture</c>, so the documented read is an indirection through that
    /// key. When it is absent the object is scanned for the first property whose name ends in
    /// <c>.context_length</c>, taking its prefix as the architecture; that recovers the value from a body
    /// whose <c>general.architecture</c> is missing without having to guess an architecture name.
    /// <para>
    /// Lengths are range-checked through <see cref="JsonElement.TryGetInt32"/> rather than
    /// <c>GetInt32</c>, so a value too large for <see cref="int"/> - or one stored as a JSON string -
    /// reads as absent instead of throwing inside a best-effort probe.
    /// </para>
    /// </remarks>
    private static (string? Architecture, int? ContextLength) ReadOllamaModelInfo(JsonElement root)
    {
        if (!root.TryGetProperty("model_info", out var modelInfo) || modelInfo.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var architecture = modelInfo.TryGetProperty("general.architecture", out var archElement)
            && archElement.ValueKind == JsonValueKind.String
            ? archElement.GetString()
            : null;

        if (!string.IsNullOrWhiteSpace(architecture)
            && modelInfo.TryGetProperty($"{architecture}.context_length", out var keyed)
            && keyed.ValueKind == JsonValueKind.Number
            && keyed.TryGetInt32(out var keyedLength))
        {
            return (architecture, keyedLength);
        }

        foreach (var property in modelInfo.EnumerateObject())
        {
            if (!property.NameEquals("general.context_length")
                && property.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var scannedLength))
            {
                var prefix = property.Name[..^".context_length".Length];
                return (architecture ?? prefix, scannedLength);
            }
        }

        return (architecture, null);
    }

    /// <summary>
    /// What one Ollama <c>/api/show</c> probe recovered. Every member is independently nullable: a body can
    /// carry a template with no <c>model_info</c>, or the reverse.
    /// </summary>
    /// <param name="Template">The literal chat template, or <see langword="null"/> when the body named none.</param>
    /// <param name="Architecture">The reported architecture, or <see langword="null"/>.</param>
    /// <param name="ContextLength">The reported context window in tokens, or <see langword="null"/>.</param>
    private readonly record struct OllamaShowMetadata(string? Template, string? Architecture, int? ContextLength);

    /// <summary>
    /// Reads a response body as UTF-8, or returns <see langword="null"/> if it exceeds
    /// <see cref="MaxShowResponseBytes"/>.
    /// </summary>
    /// <remarks>
    /// Reads the stream rather than trusting <c>Content-Length</c>, which Ollama omits under chunked
    /// transfer encoding - a header check alone would enforce nothing on exactly the responses it was meant
    /// to bound. One extra byte is requested past the cap so "filled the buffer" can be distinguished from
    /// "is exactly at the limit".
    /// </remarks>
    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[MaxShowResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > MaxShowResponseBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// Reads a model's architecture and context window from LM Studio's native
    /// <c>GET /api/v0/models</c>, or returns defaults when the endpoint could not answer for it.
    /// </summary>
    private async Task<LmStudioModelMetadata> TryReadLmStudioModelAsync(
        ProviderOptions provider, string modelId, CancellationToken cancellationToken)
    {
        var root = ProviderUrlBuilder.StripVersionSuffix(provider.BaseUrl);
        if (!Uri.TryCreate($"{root}/api/v0/models", UriKind.Absolute, out var target))
        {
            return default;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            ProviderCredentialResolver.ApplyToRequest(request, provider, _environment);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return default;
            }

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !string.Equals(id.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var architecture = entry.TryGetProperty("arch", out var arch) && arch.ValueKind == JsonValueKind.String
                    ? arch.GetString()
                    : null;

                return new LmStudioModelMetadata(architecture, ReadLmStudioContextLength(entry));
            }

            return default;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Reads a model entry's context window, preferring what the runtime will actually accept over what the
    /// model was trained for.
    /// </summary>
    /// <remarks>
    /// LM Studio reports both: <c>max_context_length</c> is the trained ceiling, while
    /// <c>loaded_context_length</c> is the window the model was actually loaded with, and a user can load a
    /// 128k model at 8k. The loaded value is preferred because the failure modes are asymmetric -
    /// under-reporting makes a client truncate a prompt it could have sent, while over-reporting makes the
    /// upstream reject one outright, which is both worse and harder to attribute. <c>max</c> is the
    /// fallback for an unloaded model, where no loaded window exists to report.
    /// </remarks>
    private static int? ReadLmStudioContextLength(JsonElement entry)
    {
        if (entry.TryGetProperty("loaded_context_length", out var loaded)
            && loaded.ValueKind == JsonValueKind.Number
            && loaded.TryGetInt32(out var loadedLength)
            && loadedLength > 0)
        {
            return loadedLength;
        }

        return entry.TryGetProperty("max_context_length", out var max)
            && max.ValueKind == JsonValueKind.Number
            && max.TryGetInt32(out var maxLength)
            && maxLength > 0
            ? maxLength
            : null;
    }

    /// <summary>
    /// What one LM Studio model-list probe recovered for a single model.
    /// </summary>
    /// <param name="Architecture">The reported architecture, or <see langword="null"/>.</param>
    /// <param name="ContextLength">The reported context window in tokens, or <see langword="null"/>.</param>
    private readonly record struct LmStudioModelMetadata(string? Architecture, int? ContextLength);
}

/// <summary>
/// Everything one pass of <see cref="ModelDialectResolver.ResolveAsync"/> learned about a model: how it
/// expresses a tool call, and how much context it accepts.
/// </summary>
/// <remarks>
/// The two are carried together because they come from the same probes, and separately nullable because
/// they are learned independently - a model whose chat template renders tools in an unregistered dialect
/// yields no capability but a perfectly good window, and a model on a provider that answers neither native
/// API yields a heuristic dialect and no window at all. An earlier shape returned only the nullable
/// capability, which silently discarded the window on every path that failed to classify.
/// </remarks>
/// <param name="Capability">The detected tool-call dialect, or <see langword="null"/> when no tier could classify it.</param>
/// <param name="ContextWindow">The probed context window, or <see langword="null"/> when none was reported.</param>
public sealed record ModelMetadataProbeResult(
    ModelToolCapability? Capability,
    ModelContextWindow? ContextWindow);

