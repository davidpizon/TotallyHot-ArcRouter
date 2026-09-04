using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Models;

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

    private readonly IEnvironmentVariableProvider _environment;

    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new instance of the <see cref="ProviderEndpointScanner"/> class.</summary>
    /// <param name="httpClient">Client used to issue the probes.</param>
    /// <param name="environment">Accessor used to resolve provider credentials and header env vars.</param>
    public ProviderEndpointScanner(HttpClient httpClient, IEnvironmentVariableProvider environment)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(environment);

        _httpClient = httpClient;
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
            using var response = await _httpClient.SendAsync(request: request, cancellationToken: cancellationToken)
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
            using var response = await _httpClient.SendAsync(request: request, cancellationToken: cancellationToken)
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