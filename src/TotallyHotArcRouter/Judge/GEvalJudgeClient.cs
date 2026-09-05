using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// An <see cref="IJudgeClient"/> that calls a free, OpenAI-compatible provider model configured in the
/// Providers screen (docs/router/geval-shadow-scoring-plan.md §1a; the G-Eval recipe is
/// docs/research/2303.16634v3.md). Prefers token logprobs for G-Eval's probability-weighted 1-5 scoring -
/// one inference call per score; falls back to a single-sample numeric-score parse of the message content
/// when the backbone returns no logprobs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provider-backed, not a hardcoded endpoint.</b> The backbone was originally a fixed local URL and
/// model name on <see cref="JudgeOptions"/>; both are gone. <see cref="JudgeModelSelector"/> now resolves a
/// <see cref="ResolvedModelRoute"/> per call, and this client reuses the forwarding path's own primitives
/// to reach it: <see cref="ProviderUrlBuilder.BuildPassthroughUrl"/> for the URL (whose overlap-collapsing
/// rule is what keeps a base of <c>http://localhost:1234/v1</c> from producing
/// <c>/v1/v1/chat/completions</c>, a mistake LM Studio answers with HTTP 200 and an error body) and
/// <see cref="ResolvedModelRoute.ExtraHeaders"/> for credentials, already resolved through the
/// literal → env-var → secret-store precedence. This deliberately does <em>not</em> loop back through our
/// own proxy: a judge call re-entering <c>ProxyMiddleware</c> would itself be graded and would enqueue a
/// further judging job.
/// </para>
/// <b>Scoped down from the plan for G1</b> (documented deviation, per AGENTS.md's deviation-recording
/// rule; mirrored in docs/router/geval-shadow-scoring-plan.md and src/PLAN.md's Settled deferrals):
/// the plan's full recipe calls for an auto-CoT evaluation-steps preamble generated once per dimension by
/// a separate LLM call and then cached with artifact-version guards. This client instead uses a static,
/// hardcoded per-dimension prompt constant (<see cref="DimensionCriteria"/>) - the task introduction,
/// criteria, and form-filling cue are still G-Eval-shaped, but the chain-of-thought evaluation steps are
/// authored once here rather than generated-and-cached. <see cref="JudgeOptions.PromptVersion"/> still
/// exists so a future move to generated-and-cached CoT is a version bump, not a schema change. Similarly,
/// the paper's n-sample estimation fallback (for backbones exposing no logprobs at all) is scoped down to
/// a single best-effort numeric parse of the response content - an acceptable G1 minimum per the plan's
/// own allowance for iteration.
/// </remarks>
public sealed class GEvalJudgeClient : IJudgeClient
{
    /// <summary>The named <see cref="HttpClient"/> this client resolves via <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = nameof(GEvalJudgeClient);

    private const int MinScore = 1;
    private const int MaxScore = 5;

    /// <summary>Generic G-Eval evaluation steps used when a dimension has no entry in <see cref="DimensionCriteria"/>.</summary>
    private const string DefaultCriteria =
        "Overall quality: correctness, clarity, and usefulness of the response for the task it was given.";

    /// <summary>
    /// The chat-completions path appended to the provider's base URL, collapsed against it by
    /// <see cref="ProviderUrlBuilder.BuildPassthroughUrl"/>.
    /// </summary>
    private const string ChatCompletionsPath = "/v1/chat/completions";

    /// <summary>
    /// Per-dimension G-Eval evaluation criteria, authored once as a static prompt fragment rather than
    /// generated via auto-CoT and cached (see this type's remarks for why). Keys match the router's
    /// dimension labels (docs/router/self-organizing-classification-plan.md's heuristic classifier
    /// output); an unrecognized dimension falls back to <see cref="DefaultCriteria"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DimensionCriteria =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["algorithm"] =
                "Algorithmic correctness: does the response solve the stated problem with a sound approach, correct edge-case handling, and reasonable complexity?",
            ["bug_fixing"] =
                "Bug-fixing quality: does the response correctly identify and fix the described defect without introducing new ones or changing unrelated behavior?",
            ["test_generation"] =
                "Test quality: do the generated tests meaningfully exercise the described behavior, including edge cases, and would they actually fail on a broken implementation?",
            ["code_review"] =
                "Review quality: does the response identify real issues, explain them clearly, and avoid flagging non-issues?",
            ["design"] =
                "Design quality: is the proposed design coherent, addresses the stated requirements, and reasonably justifies its tradeoffs?",
            ["explanation"] =
                "Explanation quality: is the explanation accurate, clear, and appropriately complete for the question asked?"
        };

    // ReSharper disable once RedundantVerbatimStringPrefix
    // Kept on every regex literal even when the current pattern has no backslash: it is what stops
    // a later `\d` or `\s` from being read as a C# escape instead of a regex one.
    private static readonly Regex ScoreDigitPattern = new(pattern: @"[1-5]", options: RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GEvalJudgeClient> _logger;
    private readonly JudgeModelSelector _modelSelector;
    private readonly IOptionsMonitor<JudgeOptions> _options;

    /// <summary>Initializes a new instance of the <see cref="GEvalJudgeClient"/> class.</summary>
    /// <param name="httpClientFactory">
    /// Supplies the named <see cref="HttpClientName"/> client used to reach the judge
    /// backbone.
    /// </param>
    /// <param name="modelSelector">Resolves which free provider model to call for each score.</param>
    /// <param name="options">Supplies the request timeout, read live so a settings change needs no restart.</param>
    /// <param name="logger">The logger.</param>
    public GEvalJudgeClient(
        IHttpClientFactory httpClientFactory,
        JudgeModelSelector modelSelector,
        IOptionsMonitor<JudgeOptions> options,
        ILogger<GEvalJudgeClient> logger)
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
    public async Task<JudgeScoreResult?> ScoreAsync(JudgeScoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var route = _modelSelector.Resolve();
        if (route is null) return null;

        var prompt = BuildPrompt(dimension: request.Dimension, responseText: request.ResponseText,
            taskPrompt: request.Prompt);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(_options.CurrentValue.RequestTimeoutSeconds);

        // ProviderModelId, not ModelName: the client-facing name is this router's alias for the route, while
        // the upstream only recognizes its own identifier.
        var chatRequest = new ChatCompletionRequest(
            Model: route.ProviderModelId,
            Messages: [new ChatMessage(Role: "user", Content: prompt)],
            0,
            true,
            5,
            8);

        var url = ProviderUrlBuilder.BuildPassthroughUrl(baseUrl: route.UpstreamBaseUrl,
            requestPath: ChatCompletionsPath, null);
        using var httpRequest = new HttpRequestMessage(method: HttpMethod.Post, requestUri: url);
        httpRequest.Content = JsonContent.Create(inputValue: chatRequest,
            jsonTypeInfo: JudgeJsonContext.Default.ChatCompletionRequest);

        // Applied by hand rather than via ProviderCredentialResolver.ApplyToRequest: the route already
        // carries these values resolved, so re-resolving them would need this client to hold an
        // IProviderConfigStore and an IEnvironmentVariableProvider it otherwise has no use for.
        foreach (var (name, value) in route.ExtraHeaders)
            if (!httpRequest.Headers.TryAddWithoutValidation(name: name, value: value))
                // Only the name is logged - the value may be the provider's credential.
                _logger.LogWarning(
                    message:
                    "Judge provider {Provider} configures header {HeaderName}, which HTTP rejected as a malformed name; the request proceeds without it.",
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
                $"Judge backbone '{route.ModelName}' returned a response carrying no choices.");

        var weighted = TryComputeWeightedScore(choice.Logprobs?.Content);
        if (weighted is { } weightedScore)
            return new JudgeScoreResult(Score: weightedScore, true, JudgeModel: route.ModelName);

        var fallback = TryParseFallbackScore(choice.Message?.Content);
        if (fallback is { } fallbackScore)
        {
            _logger.LogDebug(
                message: "Judge backbone {JudgeModel} returned no usable logprobs; used single-sample fallback parse.",
                route.ModelName);
            return new JudgeScoreResult(Score: fallbackScore, false, JudgeModel: route.ModelName);
        }

        throw new InvalidOperationException(
            $"Judge backbone '{route.ModelName}' returned no parseable score (no logprobs, no numeric content).");
    }

    /// <summary>
    /// Composes the G-Eval-shaped prompt: task introduction, the requirement the response was written for
    /// (when known), per-dimension criteria, evaluation steps, and a form-filling cue asking for a single
    /// 1-5 digit.
    /// </summary>
    /// <remarks>
    /// <paramref name="taskPrompt"/> closes the gap docs/research/code-quality-metrics-assessment.md §1
    /// names first: without it, a complete, warning-free response to a <em>different</em> question than the
    /// one asked would score identically to a correct answer to this one. Omitted from the prompt entirely
    /// when unavailable (aged out of <see cref="PendingPromptCache"/>, or never cached) rather than filled
    /// with a placeholder, so the judge is not told a task existed when none could be recovered.
    /// </remarks>
    private static string BuildPrompt(string dimension, string responseText, string taskPrompt)
    {
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
        // Nullable annotations are a compile-time contract, not a runtime guarantee - the dimension
        // originates in scored telemetry, where it can be absent.
        var criteria = DimensionCriteria.TryGetValue(key: dimension ?? string.Empty, value: out var dimensionCriteria)
            ? dimensionCriteria
            : DefaultCriteria;

        var taskSection = string.IsNullOrWhiteSpace(taskPrompt)
            ? string.Empty
            : $"""

               Task the response was written for:
               ---
               {taskPrompt}
               ---

               """;

        return $"""
                You are an expert evaluator. Your task is to rate the quality of an AI assistant's response on a
                scale of 1 (worst) to 5 (best), according to the following criterion:

                {criteria}
                {taskSection}
                Evaluation steps:
                1. Read the response carefully.
                2. Judge it strictly against the criterion above (and, when given, the task above), not against
                   unrelated qualities.
                3. Decide on a single integer score from 1 to 5.

                Response to evaluate:
                ---
                {responseText}
                ---

                Respond with only a single digit from 1 to 5 and nothing else.
                """;
    }

    /// <summary>
    /// Implements G-Eval's probability-weighted scoring: finds the first logprobs-carrying token whose text
    /// parses as a 1-5 digit, then computes <c>Σ(score_i · p_i) / Σp_i</c> over that token's candidate list
    /// (falling back to the sampled token alone when no candidate list is present), normalized to
    /// <c>[0, 1]</c>. Returns <see langword="null"/> when no logprobs are present or no candidate parses as
    /// a score digit - the caller then tries the single-sample fallback.
    /// </summary>
    private static double? TryComputeWeightedScore(IReadOnlyList<TokenLogprob>? content)
    {
        if (content is null || content.Count == 0) return null;

        foreach (var token in content)
        {
            if (!TryParseScoreDigit(token: token.Token, digit: out _)) continue;

            var candidates = new List<(int Digit, double Logprob)>();
            if (token.TopLogprobs is { Count: > 0 })
                foreach (var candidate in token.TopLogprobs)
                    if (TryParseScoreDigit(token: candidate.Token, digit: out var digit))
                        candidates.Add((digit, candidate.Logprob));

            if (candidates.Count == 0 && TryParseScoreDigit(token: token.Token, digit: out var sampledDigit))
                candidates.Add((sampledDigit, token.Logprob));

            if (candidates.Count == 0) continue;

            double sumProbability = 0;
            double weightedSum = 0;
            foreach (var (digit, logprob) in candidates)
            {
                var probability = Math.Exp(logprob);
                sumProbability += probability;
                weightedSum += digit * probability;
            }

            if (sumProbability <= 0) continue;

            return Normalize(weightedSum / sumProbability);
        }

        return null;
    }

    /// <summary>
    /// The single-sample fallback: parses the first 1-5 digit found anywhere in the message content, used
    /// only when the backbone returned no usable logprobs.
    /// </summary>
    private static double? TryParseFallbackScore(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var match = ScoreDigitPattern.Match(content);
        return match.Success ? Normalize(int.Parse(s: match.Value, provider: CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>Attempts to parse a token's trimmed text as a score digit in <c>[1, 5]</c>.</summary>
    private static bool TryParseScoreDigit(string? token, out int digit)
    {
        digit = 0;
        var trimmed = token?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length != 1) return false;

        if (!int.TryParse(s: trimmed, result: out var value) || value < MinScore || value > MaxScore) return false;

        digit = value;
        return true;
    }

    /// <summary>Maps a raw 1-5 mean score onto <c>[0, 1]</c>.</summary>
    private static double Normalize(double meanScore)
    {
        return Math.Clamp(value: (meanScore - MinScore) / (MaxScore - MinScore), 0.0, 1.0);
    }
}

/// <summary>The OpenAI-compatible chat-completions request body <see cref="GEvalJudgeClient"/> sends.</summary>
internal sealed record ChatCompletionRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")]
    IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("temperature")]
    double Temperature,
    [property: JsonPropertyName("logprobs")]
    bool Logprobs,
    [property: JsonPropertyName("top_logprobs")]
    int TopLogprobs,
    [property: JsonPropertyName("max_tokens")]
    int MaxTokens);

/// <summary>One chat message in an OpenAI-compatible request.</summary>
internal sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")]
    string Content);

/// <summary>The OpenAI-compatible chat-completions response body (only the fields <see cref="GEvalJudgeClient"/> reads).</summary>
internal sealed record ChatCompletionResponse(
    [property: JsonPropertyName("choices")]
    IReadOnlyList<ChatChoice>? Choices);

/// <summary>One completion choice.</summary>
internal sealed record ChatChoice(
    [property: JsonPropertyName("message")]
    ChatMessage? Message,
    [property: JsonPropertyName("logprobs")]
    ChoiceLogprobs? Logprobs);

/// <summary>A choice's logprobs block.</summary>
internal sealed record ChoiceLogprobs(
    [property: JsonPropertyName("content")]
    IReadOnlyList<TokenLogprob>? Content);

/// <summary>One generated token's logprob and its top alternative-token candidates.</summary>
internal sealed record TokenLogprob(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("logprob")]
    double Logprob,
    [property: JsonPropertyName("top_logprobs")]
    IReadOnlyList<TopLogprobCandidate>? TopLogprobs);

/// <summary>One candidate alternative token and its logprob, from a token's <c>top_logprobs</c> list.</summary>
internal sealed record TopLogprobCandidate(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("logprob")]
    double Logprob);

/// <summary>Source-generated JSON contract for the judge backbone's request/response DTOs.</summary>
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
internal sealed partial class JudgeJsonContext : JsonSerializerContext;