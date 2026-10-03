using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Probes a provider's well-known paths to discover which API flavors its endpoint actually answers
/// (<c>docs/router/tool-call-normalization.md</c> §3.3, Phase 2).
/// <para>
/// Routing continues to speak OpenAI-compatible only. The native flavors are recorded because they expose
/// model metadata the OpenAI-shaped endpoint does not - Ollama's <c>/api/show</c> returns the literal chat
/// template, which is the cheapest and most reliable source of a model's tool-call dialect. So this scan
/// pays for itself immediately through detection rather than being groundwork for a future routing change.
/// </para>
/// <para>
/// Every probe is independent and best-effort, following <c>ManagementFacade</c>'s existing
/// <c>DiscoverModelsCoreAsync</c>: credentials and the provider's own custom headers are applied exactly as
/// the forwarding path applies them (which is how an Anthropic provider's required
/// <c>anthropic-version</c> reaches the probe with no provider-specific code here), and an unreachable host
/// records all-false plus an error rather than throwing.
/// </para>
/// </summary>
public sealed class ProviderEndpointScanner
{
    /// <summary>
    /// A model id no real deployment will recognize, sent in the opt-in <c>/v1/messages</c> probe. Chosen
    /// so the probe's own request can never accidentally name a model that actually exists and gets
    /// generated against - see <see cref="ProbeAnthropicMessagesAsync"/>.
    /// </summary>
    private const string AnthropicProbeModel = "arcrouter-capability-probe";

    /// <summary>
    /// The opt-in <c>/v1/messages</c> probe's request body: the sentinel model id above, a one-token budget,
    /// and the shortest well-formed user message. Fixed and literal because it never varies - see
    /// <see cref="ProbeAnthropicMessagesAsync"/> for why a small, static payload is the point.
    /// </summary>
    private const string AnthropicMessagesProbeBody =
        $$"""{"model":"{{AnthropicProbeModel}}","max_tokens":1,"messages":[{"role":"user","content":"hi"}]}""";

    /// <summary>
    /// The page size <see cref="ScanModelFeaturesAsync"/> asks for: Anthropic's documented maximum for
    /// <c>GET /v1/models</c>, whose default page is only 20 models.
    /// </summary>
    private const int ModelListPageSize = 1000;

    /// <summary>
    /// How many pages <see cref="ScanModelFeaturesAsync"/> reads before giving up on a list as incomplete - a
    /// bound against an endpoint that keeps answering <c>has_more: true</c>.
    /// </summary>
    private const int MaxModelListPages = 10;

    private readonly IEnvironmentVariableProvider _environment;

    private readonly HttpClient? _httpClient;
    private readonly IHttpClientFactory? _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="ProviderEndpointScanner"/> class.</summary>
    /// <param name="environment">Accessor used to resolve provider credentials and header env vars.</param>
    /// <param name="httpClient">Client used to issue the probes when no factory is supplied.</param>
    /// <param name="httpClientFactory">
    /// Creates a fresh <see cref="ManagementFacade.HttpClientName"/> client per probe. Required when
    /// <paramref name="httpClient"/> is omitted.
    /// </param>
    public ProviderEndpointScanner(IEnvironmentVariableProvider environment, HttpClient? httpClient = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (httpClient is null && httpClientFactory is null)
            throw new ArgumentNullException(nameof(httpClientFactory));

        _httpClient = httpClient;
        _httpClientFactory = httpClientFactory;
        _environment = environment;
    }

    /// <summary>
    /// Probes <paramref name="provider"/>'s endpoint and reports which flavors answered.
    /// </summary>
    /// <param name="providerKey">The <c>ModelRouting:Providers</c> key being scanned.</param>
    /// <param name="provider">The provider's connection details.</param>
    /// <param name="cancellationToken">Cancels the probes.</param>
    /// <returns>
    /// Always a result for every runtime outcome. A provider whose base URL is malformed, that is
    /// unreachable, that times out, or that answers nothing reports every flag false with
    /// <see cref="ProviderEndpointCapabilities.ScanError"/> set rather than throwing - callers persist this
    /// result, so a network condition must never surface as an exception.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="providerKey"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    /// <remarks>
    /// The argument guards above are the deliberate exception to the fail-open contract: they signal a
    /// programming error at the call site, not a condition of the provider being scanned, and returning a
    /// <c>ScanError</c> for them would record a fabricated observation about a provider that was never
    /// actually probed.
    /// </remarks>
    public async Task<ProviderEndpointCapabilities> ScanAsync(
        string providerKey,
        ProviderOptions provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(provider);

        if (!Uri.TryCreate(uriString: provider.BaseUrl, uriKind: UriKind.Absolute, result: out _))
            return new ProviderEndpointCapabilities(
                ProviderKey: providerKey, false, false, false, false, false, ScannedAtUtc: DateTimeOffset.UtcNow,
                ScanError: $"Invalid BaseUrl: '{provider.BaseUrl}'.");

        var root = ProviderUrlBuilder.StripVersionSuffix(provider.BaseUrl);

        // The OpenAI-shaped list is reused to detect Anthropic too: both answer GET /v1/models, and they are
        // told apart by response shape rather than by a second request (see ClassifyModelsBody).
        var openAiProbe = await ProbeAsync(url: ProviderUrlBuilder.BuildModelsUrl(provider.BaseUrl), provider: provider,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var lmStudioProbe =
            await ProbeAsync(url: $"{root}/api/v0/models", provider: provider, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        var ollamaProbe =
            await ProbeAsync(url: $"{root}/api/tags", provider: provider, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        var (openAiCompatible, anthropicCompatible, unrecognizedBody) = ClassifyModelsBody(openAiProbe);

        // Opt-in only, and only when the model list didn't already answer the question - see
        // ProviderOptions.ProbeAnthropicMessages and ProbeAnthropicMessagesAsync for why this probe is not
        // unconditional like the three GET probes above.
        string? messagesProbeError = null;
        if (!anthropicCompatible && provider.ProbeAnthropicMessages)
        {
            var messagesProbe =
                await ProbeAnthropicMessagesAsync(provider: provider, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            anthropicCompatible = messagesProbe.Compatible;
            messagesProbeError = messagesProbe.Error;
        }

        var anyAnswered = openAiCompatible || anthropicCompatible || lmStudioProbe.Succeeded || ollamaProbe.Succeeded;

        // A 2xx whose body is not a model list is the most confusing failure mode to debug, and the one the
        // probe's own Error field cannot describe - HTTP-wise it succeeded, so it carries no error at all.
        // Without this, a captive portal or reverse proxy answering 200 with HTML would show up as nothing
        // but the two expected native 404s, making a responding endpoint look entirely unreachable.
        var openAiFailure = unrecognizedBody ?? openAiProbe.Error;

        return new ProviderEndpointCapabilities(
            ProviderKey: providerKey,
            OpenAiCompatible: openAiCompatible,
            LmStudioNative: lmStudioProbe.Succeeded,
            OllamaNative: ollamaProbe.Succeeded,
            AnthropicCompatible: anthropicCompatible,
            // Derived from the two native flavors rather than probed directly - see the field's own docs on
            // ProviderEndpointCapabilities for why a live POST probe would JIT-load a model and why a plain
            // OpenAI-compatible endpoint deliberately does not count.
            JsonSchemaResponseFormat: lmStudioProbe.Succeeded || ollamaProbe.Succeeded,
            ScannedAtUtc: DateTimeOffset.UtcNow,
            // Only surfaced when nothing answered at all. A provider that speaks OpenAI but has no native
            // endpoints is completely healthy, and reporting the two expected 404s as an error would make
            // every normal cloud provider look broken.
            ScanError: anyAnswered
                ? null
                : SummarizeFailure(openAiFailure, lmStudioProbe.Error, ollamaProbe.Error, messagesProbeError));
    }

    /// <summary>
    /// Reads the per-model <c>capabilities</c> records from an Anthropic-shaped model list
    /// (<c>docs/adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md</c> Amendment 1), following its
    /// pagination to the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate step from <see cref="ScanAsync"/>, called only once that scan says the endpoint is
    /// Anthropic-compatible, for the same reason per-model metadata resolution is separate: endpoint flavors and
    /// per-model facts have different consumers and different failure handling. It costs one extra
    /// <c>GET</c> per scan of such a provider, never one per request.
    /// </para>
    /// <para>
    /// All or nothing. Any page that fails, is not JSON, or is not Anthropic-shaped makes the whole result
    /// <see langword="null"/>, and so does a list longer than <see cref="MaxModelListPages"/> pages. The caller then
    /// keeps whatever it recorded before: replacing a provider's records with a partial list would drop the models
    /// on the unread pages, turning a transient failure into lost knowledge. A model whose entry has no
    /// <c>capabilities</c> object (absent or <c>null</c>) gets no record, which reads as unknown.
    /// </para>
    /// </remarks>
    /// <param name="providerKey">The <c>ModelRouting:Providers</c> key the records are stored under.</param>
    /// <param name="provider">The provider to read, with its credentials and custom headers.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>Every record the complete list reported, or <see langword="null"/> when the list could not be read.</returns>
    public async Task<IReadOnlyList<ModelFeatureSupport>?> ScanModelFeaturesAsync(
        string providerKey,
        ProviderOptions provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(provider);

        if (!Uri.TryCreate(uriString: provider.BaseUrl, uriKind: UriKind.Absolute, result: out _)) return null;

        var modelsUrl = ProviderUrlBuilder.BuildModelsUrl(provider.BaseUrl);
        var scannedAtUtc = DateTimeOffset.UtcNow;
        var records = new List<ModelFeatureSupport>();
        string? afterId = null;

        for (var page = 0; page < MaxModelListPages; page++)
        {
            var url = afterId is null
                ? $"{modelsUrl}?limit={ModelListPageSize}"
                : $"{modelsUrl}?limit={ModelListPageSize}&after_id={Uri.EscapeDataString(afterId)}";

            var probe = await ProbeAsync(url: url, provider: provider, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!probe.Succeeded || probe.Body is null) return null;

            var next = ReadModelFeaturePage(body: probe.Body, providerKey: providerKey, scannedAtUtc: scannedAtUtc,
                records: records);
            if (next is null) return null;
            if (!next.Value.HasMore) return records;

            afterId = next.Value.LastId;
            if (afterId is null) return null;
        }

        return null;
    }

    /// <summary>
    /// Parses one page of an Anthropic-shaped model list, appending a record for every entry that carries a
    /// <c>capabilities</c> object.
    /// </summary>
    /// <returns>
    /// Whether more pages follow and the id to continue after, or <see langword="null"/> when the body is not an
    /// Anthropic-shaped model list - see <see cref="ScanModelFeaturesAsync"/> for why that voids the whole scan.
    /// </returns>
    private static (bool HasMore, string? LastId)? ReadModelFeaturePage(
        string body, string providerKey, DateTimeOffset scannedAtUtc, List<ModelFeatureSupport> records)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(propertyName: "data", value: out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty(propertyName: "has_more", value: out var hasMore) ||
                hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            foreach (var entry in data.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty(propertyName: "id", value: out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()) &&
                    entry.TryGetProperty(propertyName: "capabilities", value: out var capabilities) &&
                    capabilities.ValueKind == JsonValueKind.Object)
                    records.Add(new ModelFeatureSupport(ProviderKey: providerKey, ModelId: id.GetString()!,
                        Capabilities: capabilities.Clone(), ScannedAtUtc: scannedAtUtc));

            var lastId = root.TryGetProperty(propertyName: "last_id", value: out var last) &&
                         last.ValueKind == JsonValueKind.String
                ? last.GetString()
                : null;

            return (hasMore.GetBoolean(), lastId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decides which of the two <c>/v1/models</c>-shaped flavors answered, from the response body.
    /// </summary>
    /// <remarks>
    /// Anthropic and OpenAI both serve <c>GET /v1/models</c> with a <c>data</c> array, so a status code
    /// alone cannot separate them. Anthropic's list is paginated and carries <c>has_more</c> (and
    /// <c>first_id</c>/<c>last_id</c>) at the root, which OpenAI's does not - that is the discriminator.
    /// A body carrying neither marker is treated as OpenAI-compatible, the safe default: it is the flavor
    /// routing actually uses, so a false negative there would be far more disruptive than a false positive
    /// on a flag nothing reads yet.
    /// </remarks>
    /// <returns>
    /// The two flavor flags, plus - when the endpoint answered 2xx with something that is not a model list -
    /// a description of why the body was rejected, so <c>ScanError</c> can say so rather than staying silent
    /// about a response that did arrive.
    /// </returns>
    private static (bool OpenAiCompatible, bool AnthropicCompatible, string? UnrecognizedBody) ClassifyModelsBody(
        ProbeResult probe)
    {
        if (!probe.Succeeded || probe.Body is null) return (false, false, null);

        try
        {
            using var document = JsonDocument.Parse(probe.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(propertyName: "data", value: out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return (false, false,
                    "The models endpoint returned a success status but no 'data' array; it does not look like a model list.");

            var looksAnthropic = document.RootElement.TryGetProperty(propertyName: "has_more", value: out _)
                                 || document.RootElement.TryGetProperty(propertyName: "first_id", value: out _);

            return looksAnthropic ? (false, true, null) : (true, false, null);
        }
        catch (JsonException)
        {
            // A 200 whose body is not JSON is not a model list, whatever it is - most often a captive
            // portal, a reverse proxy error page, or an SSO redirect landing page.
            return (false, false, "The models endpoint returned a success status but the body was not valid JSON.");
        }
    }

    /// <summary>
    /// The opt-in probe for Anthropic Messages API support that <c>GET /v1/models</c> cannot see: a local
    /// runtime whose model list answers in plain OpenAI shape but that separately understands
    /// <c>POST /v1/messages</c> too (e.g. a recent LM Studio build).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only called when <see cref="ProviderOptions.ProbeAnthropicMessages"/> is set - see that property's
    /// docs for why this probe, unlike every GET above, defaults to off. The sentinel
    /// <see cref="AnthropicProbeModel"/> id and one-token budget keep it lightweight even when a
    /// misconfigured or unusually permissive endpoint does resolve it: at worst, one real token generates.
    /// </para>
    /// <para>
    /// Classified by response shape rather than status code, the same approach <see cref="ClassifyModelsBody"/>
    /// uses: the sentinel model does not exist on a real deployment, so the expected outcome is a rejection,
    /// and only a rejection shaped like Anthropic's own error envelope
    /// (<c>{"type":"error","error":{"type":...}}</c>) - or, on a passthrough that resolves the id anyway, an
    /// actual message response (<c>{"type":"message",...}</c>) - counts as compatible. Any other shape,
    /// including a generic 404 or an OpenAI-style error body, means the endpoint does not speak this dialect.
    /// </para>
    /// </remarks>
    private async Task<(bool Compatible, string? Error)> ProbeAnthropicMessagesAsync(
        ProviderOptions provider, CancellationToken cancellationToken)
    {
        var url = ProviderUrlBuilder.BuildMessagesUrl(provider.BaseUrl);
        if (!Uri.TryCreate(uriString: url, uriKind: UriKind.Absolute, result: out var target))
            return (false, $"Invalid probe URL '{url}'.");

        using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: target);
        request.Content = new StringContent(content: AnthropicMessagesProbeBody, encoding: Encoding.UTF8,
            mediaType: "application/json");

        var rejectedHeaders =
            ProviderCredentialResolver.ApplyToRequest(request: request, provider: provider, environment: _environment);

        try
        {
            using var factoryClient = _httpClientFactory?.CreateClient(ManagementFacade.HttpClientName);
            var client = factoryClient ?? _httpClient!;
            using var response = await client.SendAsync(request: request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (ClassifyMessagesBody(succeeded: response.IsSuccessStatusCode, body: body)) return (true, null);

            // Not an exception, but not a match either: the endpoint answered, just not with a body shaped
            // like Anthropic's dialect. Recorded so an opted-in probe that found nothing still shows up in
            // ScanError instead of silently vanishing when nothing else answers.
            return (false,
                Explain(
                    reason:
                    $"{target} returned {(int)response.StatusCode} with a body that did not match the Anthropic Messages API shape.",
                    rejectedHeaders: rejectedHeaders));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (false, Explain(reason: $"{target}: {ex.Message}", rejectedHeaders: rejectedHeaders));
        }
    }

    /// <summary>
    /// Decides whether a <c>/v1/messages</c> response proves the endpoint speaks the Anthropic dialect - see
    /// <see cref="ProbeAnthropicMessagesAsync"/>'s remarks for why both a success and an Anthropic-shaped
    /// error count, and a generic error does not.
    /// </summary>
    private static bool ClassifyMessagesBody(bool succeeded, string? body)
    {
        if (string.IsNullOrEmpty(body)) return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            if (succeeded)
                return document.RootElement.TryGetProperty(propertyName: "type", value: out var messageType)
                       && messageType.ValueEquals("message");

            return document.RootElement.TryGetProperty(propertyName: "type", value: out var errorType)
                   && errorType.ValueEquals("error")
                   && document.RootElement.TryGetProperty(propertyName: "error", value: out var errorObject)
                   && errorObject.ValueKind == JsonValueKind.Object
                   && errorObject.TryGetProperty(propertyName: "type", value: out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Issues one GET with the provider's credentials and custom headers applied.</summary>
    private async Task<ProbeResult> ProbeAsync(string url, ProviderOptions provider,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(uriString: url, uriKind: UriKind.Absolute, result: out var target))
            return new ProbeResult(false, null, Error: $"Invalid probe URL '{url}'.");

        using var request = new HttpRequestMessage(method: HttpMethod.Get, requestUri: target);

        // Identical to the forwarding path, so a provider needing an extra header to answer at all (e.g.
        // Anthropic's anthropic-version) gets it without any provider-specific branch here. Any header name
        // HTTP refused comes back so a failure can name it rather than reading as a bad credential.
        var rejectedHeaders =
            ProviderCredentialResolver.ApplyToRequest(request: request, provider: provider, environment: _environment);

        try
        {
            using var factoryClient = _httpClientFactory?.CreateClient(ManagementFacade.HttpClientName);
            var client = factoryClient ?? _httpClient!;
            using var response = await client.SendAsync(request: request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ProbeResult(
                    false, null,
                    Error: Explain(reason: $"{target} returned {(int)response.StatusCode}.",
                        rejectedHeaders: rejectedHeaders));

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new ProbeResult(true, Body: body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new ProbeResult(false, null,
                Error: Explain(reason: $"{target}: {ex.Message}", rejectedHeaders: rejectedHeaders));
        }
    }

    /// <summary>
    /// Appends the names HTTP rejected, when there were any, so a failure caused by a malformed header name
    /// does not read as a credential or connectivity problem. Only the names are reported - their values may
    /// be secrets.
    /// </summary>
    private static string Explain(string reason, IReadOnlyList<string>? rejectedHeaders)
    {
        return rejectedHeaders is null or { Count: 0 }
            ? reason
            : $"{reason} Note: these configured header names are not valid HTTP header names and were not "
              + $"sent: {string.Join(separator: ", ", values: rejectedHeaders.Select(name => $"'{name}'"))}.";
    }

    /// <summary>Joins each probe's failure reason into one line for <c>scan_error</c>.</summary>
    private static string SummarizeFailure(params string?[] reasons)
    {
        return string.Join(separator: " | ", values: reasons.Where(r => !string.IsNullOrWhiteSpace(r)));
    }

    /// <summary>One probe's outcome: whether it answered 2xx, its body, and why it failed if it didn't.</summary>
    private sealed record ProbeResult(bool Succeeded, string? Body, string? Error);
}