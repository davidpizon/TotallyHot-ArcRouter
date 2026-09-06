using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Shared HTTP/route-resolution plumbing for every Phase Q3 portfolio grader (CodeJudge/ICE-Score/RACE).
/// Reuses the exact backbone-reaching mechanics <see cref="GEvalJudgeClient"/> already proved out - the same
/// <see cref="JudgeModelSelector"/> eligibility, the same <see cref="ProviderUrlBuilder.BuildPassthroughUrl"/>
/// URL construction, the same header/credential handling, the same OpenAI-compatible chat-completions DTOs -
/// so each concrete grader only supplies what is actually different: its prompt and how it turns the
/// backbone's answer into a <c>[0,1]</c> score.
/// </summary>
/// <remarks>
/// Deliberately simpler than <see cref="GEvalJudgeClient"/> in one respect: no logprobs-weighted scoring.
/// G-Eval's probability weighting is specific to a single 1-5 digit score; CodeJudge's fault-taxonomy output
/// and ICE-Score/RACE's rating digits are each parsed from the single-sample message content only, which the
/// literature accepts as the baseline approach these methods were themselves measured against
/// (docs/research/code-quality-metrics-assessment.md §5.1).
/// </remarks>
public abstract class PortfolioGraderClientBase : IPortfolioGraderClient
{
    /// <summary>The chat-completions path appended to the provider's base URL, shared with <see cref="GEvalJudgeClient"/>.</summary>
    private const string ChatCompletionsPath = "/v1/chat/completions";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly JudgeModelSelector _modelSelector;
    private readonly IOptionsMonitor<JudgeOptions> _options;

    /// <summary>Initializes the shared collaborators every concrete portfolio grader needs.</summary>
    /// <param name="httpClientFactory">Supplies the named HTTP client used to reach the backbone.</param>
    /// <param name="modelSelector">Resolves which free provider model to call for each score.</param>
    /// <param name="options">Supplies the request timeout, shared with the G-Eval judge.</param>
    /// <param name="logger">The logger.</param>
    protected PortfolioGraderClientBase(
        IHttpClientFactory httpClientFactory,
        JudgeModelSelector modelSelector,
        IOptionsMonitor<JudgeOptions> options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(modelSelector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _modelSelector = modelSelector;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public abstract string GraderKey { get; }

    /// <summary>The named <see cref="HttpClient"/> this grader resolves via <see cref="IHttpClientFactory"/>.</summary>
    protected abstract string HttpClientName { get; }

    /// <inheritdoc/>
    public async Task<double?> ScoreAsync(PortfolioGraderScoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var route = _modelSelector.Resolve();
        if (route is null) return null;

        var prompt = BuildPrompt(dimension: request.Dimension, responseText: request.ResponseText,
            taskPrompt: request.Prompt);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(_options.CurrentValue.RequestTimeoutSeconds);

        var chatRequest = new ChatCompletionRequest(
            Model: route.ProviderModelId,
            Messages: [new ChatMessage(Role: "user", Content: prompt)],
            0,
            false,
            0,
            256);

        var url = ProviderUrlBuilder.BuildPassthroughUrl(baseUrl: route.UpstreamBaseUrl,
            requestPath: ChatCompletionsPath, null);
        using var httpRequest = new HttpRequestMessage(method: HttpMethod.Post, requestUri: url);
        httpRequest.Content = JsonContent.Create(inputValue: chatRequest,
            jsonTypeInfo: JudgeJsonContext.Default.ChatCompletionRequest);

        foreach (var (name, value) in route.ExtraHeaders)
            if (!httpRequest.Headers.TryAddWithoutValidation(name: name, value: value))
                _logger.LogWarning(
                    message:
                    "Portfolio grader {GraderKey} provider {Provider} configures header {HeaderName}, which HTTP rejected as a malformed name; the request proceeds without it.",
                    GraderKey,
                    route.Provider,
                    name);

        using var response = await client.SendAsync(request: httpRequest, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var parsed = await response.Content
            .ReadFromJsonAsync(jsonTypeInfo: JudgeJsonContext.Default.ChatCompletionResponse,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var choice = parsed?.Choices?.FirstOrDefault();
        if (choice is null)
            throw new InvalidOperationException(
                $"Portfolio grader '{GraderKey}' backbone '{route.ModelName}' returned a response carrying no choices.");

        var score = TryParseScore(choice.Message?.Content);
        if (score is { } parsedScore) return Math.Clamp(value: parsedScore, 0.0, 1.0);

        throw new InvalidOperationException(
            $"Portfolio grader '{GraderKey}' backbone '{route.ModelName}' returned no parseable score.");
    }

    /// <summary>Composes this grader's prompt for one response.</summary>
    /// <param name="dimension">The task dimension the response was routed under.</param>
    /// <param name="responseText">The response text to grade.</param>
    /// <param name="taskPrompt">The task the response was written for, or empty when unrecoverable.</param>
    protected abstract string BuildPrompt(string dimension, string responseText, string taskPrompt);

    /// <summary>
    /// Parses the backbone's raw message content into a <c>[0,1]</c> score, or <see langword="null"/> when
    /// nothing in it parses - the caller then throws rather than fabricating a value, exactly as
    /// <see cref="GEvalJudgeClient"/> does for its own unparseable-content case.
    /// </summary>
    protected abstract double? TryParseScore(string? content);
}
