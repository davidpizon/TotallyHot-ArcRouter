using System.Text.Json;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Probes a provider's well-known paths to discover which API flavors its endpoint actually answers
/// (<c>docs/router/tool-call-normalization.md</c> §3.3, Phase 2).
///
/// <para>
/// Routing continues to speak OpenAI-compatible only. The native flavors are recorded because they expose
/// model metadata the OpenAI-shaped endpoint does not - Ollama's <c>/api/show</c> returns the literal chat
/// template, which is the cheapest and most reliable source of a model's tool-call dialect. So this scan
/// pays for itself immediately through detection rather than being groundwork for a future routing change.
/// </para>
///
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
    private readonly HttpClient _httpClient;
    private readonly IEnvironmentVariableProvider _environment;

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

        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _))
        {
            return new ProviderEndpointCapabilities(
                providerKey, false, false, false, false, false, DateTimeOffset.UtcNow,
                ScanError: $"Invalid BaseUrl: '{provider.BaseUrl}'.");
        }

        var root = ProviderUrlBuilder.StripVersionSuffix(provider.BaseUrl);

        // The OpenAI-shaped list is reused to detect Anthropic too: both answer GET /v1/models, and they are
        // told apart by response shape rather than by a second request (see ClassifyModelsBody).
        var openAiProbe = await ProbeAsync(ProviderUrlBuilder.BuildModelsUrl(provider.BaseUrl), provider, cancellationToken)
            .ConfigureAwait(false);
        var lmStudioProbe = await ProbeAsync($"{root}/api/v0/models", provider, cancellationToken).ConfigureAwait(false);
        var ollamaProbe = await ProbeAsync($"{root}/api/tags", provider, cancellationToken).ConfigureAwait(false);

        var (openAiCompatible, anthropicCompatible, unrecognizedBody) = ClassifyModelsBody(openAiProbe);

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
                : SummarizeFailure(openAiFailure, lmStudioProbe.Error, ollamaProbe.Error));
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
        if (!probe.Succeeded || probe.Body is null)
        {
            return (false, false, null);
        }

        try
        {
            using var document = JsonDocument.Parse(probe.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return (false, false, "The models endpoint returned a success status but no 'data' array; it does not look like a model list.");
            }

            var looksAnthropic = document.RootElement.TryGetProperty("has_more", out _)
                || document.RootElement.TryGetProperty("first_id", out _);

            return looksAnthropic ? (false, true, null) : (true, false, null);
        }
        catch (JsonException)
        {
            // A 200 whose body is not JSON is not a model list, whatever it is - most often a captive
            // portal, a reverse proxy error page, or an SSO redirect landing page.
            return (false, false, "The models endpoint returned a success status but the body was not valid JSON.");
        }
    }

    /// <summary>Issues one GET with the provider's credentials and custom headers applied.</summary>
    private async Task<ProbeResult> ProbeAsync(string url, ProviderOptions provider, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
        {
            return new ProbeResult(false, null, $"Invalid probe URL '{url}'.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, target);

        // Identical to the forwarding path, so a provider needing an extra header to answer at all (e.g.
        // Anthropic's anthropic-version) gets it without any provider-specific branch here. Any header name
        // HTTP refused comes back so a failure can name it rather than reading as a bad credential.
        var rejectedHeaders = ProviderCredentialResolver.ApplyToRequest(request, provider, _environment);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(
                    false, null, Explain($"{target} returned {(int)response.StatusCode}.", rejectedHeaders));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new ProbeResult(true, body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new ProbeResult(false, null, Explain($"{target}: {ex.Message}", rejectedHeaders));
        }
    }

    /// <summary>
    /// Appends the names HTTP rejected, when there were any, so a failure caused by a malformed header name
    /// does not read as a credential or connectivity problem. Only the names are reported - their values may
    /// be secrets.
    /// </summary>
    private static string Explain(string reason, IReadOnlyList<string>? rejectedHeaders) =>
        rejectedHeaders is null or { Count: 0 }
            ? reason
            : $"{reason} Note: these configured header names are not valid HTTP header names and were not "
                + $"sent: {string.Join(", ", rejectedHeaders.Select(name => $"'{name}'"))}.";

    /// <summary>Joins each probe's failure reason into one line for <c>scan_error</c>.</summary>
    private static string SummarizeFailure(params string?[] reasons) =>
        string.Join(" | ", reasons.Where(r => !string.IsNullOrWhiteSpace(r)));

    /// <summary>One probe's outcome: whether it answered 2xx, its body, and why it failed if it didn't.</summary>
    private sealed record ProbeResult(bool Succeeded, string? Body, string? Error);
}

