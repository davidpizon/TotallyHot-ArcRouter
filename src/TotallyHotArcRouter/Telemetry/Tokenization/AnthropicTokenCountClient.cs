using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// Reads an exact, model-specific input-token count from Anthropic's <c>/v1/messages/count_tokens</c>
/// endpoint. Used only by the calibration sampler, to learn how far the local tiktoken proxy encoding
/// sits from what Anthropic actually bills (ADR-0009) - never on the estimate path, which must stay
/// offline-capable.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shaped like <see cref="AnthropicUsageReportClient"/>: same <c>anthropic-version</c>
/// header, same <see cref="CostReconciliationRetryPolicy"/>, same tolerance for a response shape that may
/// gain fields. The one material difference is the credential - this endpoint takes an ordinary inference
/// API key, <em>not</em> the Admin key that the usage report requires, so the two must never share a
/// configured secret.
/// </para>
/// <para>
/// The endpoint is free and separately rate-limited from the Messages API, which is what makes a standing
/// calibration loop affordable at all; the sampler still bounds itself to
/// <see cref="TokenizationOptions.MaxSamplesPerCycle"/> per cycle rather than relying on that.
/// </para>
/// </remarks>
public sealed class AnthropicTokenCountClient
{
    private const string BaseUrl = "https://api.anthropic.com/v1/messages/count_tokens";
    private const string AnthropicVersion = "2023-06-01";

    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AnthropicTokenCountClient>? _logger;

    /// <summary>Initializes a new instance of the <see cref="AnthropicTokenCountClient"/> class.</summary>
    /// <param name="httpClient">The transport to send on.</param>
    /// <param name="apiKey">
    /// An ordinary Anthropic inference API key. Not the Admin key - this endpoint neither needs nor accepts
    /// that elevated credential.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public AnthropicTokenCountClient(HttpClient httpClient, string apiKey,
        ILogger<AnthropicTokenCountClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _httpClient = httpClient;
        _apiKey = apiKey;
        _logger = logger;
    }

    /// <summary>
    /// Counts the input tokens <paramref name="model"/> would bill for <paramref name="text"/> sent as a
    /// single user message.
    /// </summary>
    /// <param name="model">The Anthropic model id to count against; counts are model-specific.</param>
    /// <param name="text">The prompt text to count.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The provider's own token count, or <see langword="null"/> when the call failed or returned a shape
    /// with no usable count. Never throws for a transport or protocol failure: calibration is an
    /// opportunistic background improvement, and a provider outage must degrade it to "no new sample"
    /// rather than fault the hosting service.
    /// </returns>
    /// <remarks>
    /// The count is compared against a local count of the <em>same</em> text, so the small constant
    /// Anthropic adds for message framing appears identically in every sample and is absorbed into the
    /// learned factor rather than needing to be modelled here.
    /// </remarks>
    public async Task<int?> TryCountTokensAsync(string model, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var response = await CostReconciliationRetryPolicy.SendWithRetryAsync(
                httpClient: _httpClient,
                requestFactory: () => BuildRequest(model: model, text: text),
                logger: _logger,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug(
                    "[TOKEN-CALIBRATION] count_tokens returned {StatusCode} for {Model}; skipping this sample.",
                    (int)response.StatusCode, model);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReadInputTokens(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger?.LogDebug(exception: ex,
                message: "[TOKEN-CALIBRATION] count_tokens call failed for {Model}; skipping this sample.",
                model);
            return null;
        }
    }

    /// <summary>Parses <c>input_tokens</c> out of a successful response body.</summary>
    /// <param name="body">The raw response JSON.</param>
    /// <returns>The token count, or <see langword="null"/> when the field is absent or not a positive number.</returns>
    private static int? ReadInputTokens(string body)
    {
        if (JsonNode.Parse(body) is not JsonObject root) return null;
        if (root["input_tokens"] is not JsonValue value) return null;
        if (!value.TryGetValue<int>(out var tokens) || tokens <= 0) return null;

        return tokens;
    }

    /// <summary>Builds one <c>count_tokens</c> request carrying the text as a single user message.</summary>
    /// <param name="model">The model to count against.</param>
    /// <param name="text">The prompt text.</param>
    /// <returns>The prepared request.</returns>
    private HttpRequestMessage BuildRequest(string model, string text)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text })
        };

        var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: BaseUrl)
        {
            Content = new StringContent(content: payload.ToJsonString(), encoding: Encoding.UTF8,
                mediaType: "application/json")
        };
        request.Headers.Add(name: "x-api-key", value: _apiKey);
        request.Headers.Add(name: "anthropic-version", value: AnthropicVersion);
        return request;
    }
}
