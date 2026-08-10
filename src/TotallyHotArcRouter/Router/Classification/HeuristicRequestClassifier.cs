using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Router.Classification;

/// <summary>
/// A keyword/shape heuristic <see cref="IRequestClassifier"/> - deliberately minimal to start (same
/// philosophy as <see cref="TotallyHot.ArcRouter.Sandbox.Extraction.KeywordDimensionInferrer"/>, architecture doc §10).
/// Delegates dimension inference to the shared <see cref="IDimensionInferrer"/> so a request classified
/// here and the same prompt's later sandbox-side dimension can never independently drift apart.
/// </summary>
public sealed class HeuristicRequestClassifier : IRequestClassifier
{
    /// <summary>The <c>max_tokens</c> (or <c>max_completion_tokens</c>) ceiling at or below which a
    /// request is treated as utility traffic - <c>docs/router/utility-model-routing.md</c> B2's "small
    /// max_tokens" signal. A chat-title or intent-detection call needs a handful of tokens; a normal
    /// chat turn does not cap itself this low.</summary>
    private const int UtilityMaxTokensCeiling = 64;

    /// <summary>The prompt-length ceiling (characters) below which a request is eligible to be treated
    /// as utility traffic on shape alone - B2's "short ... prompt" signal.</summary>
    private const int UtilityPromptLengthCeiling = 200;

    /// <summary>The prompt-length floor (characters) above which a request is treated as
    /// <see cref="RouterDifficulty.Hard"/> on shape alone, absent any other signal.</summary>
    private const int HardPromptLengthFloor = 1500;

    /// <summary>The prompt-length ceiling (characters) below which a request is treated as
    /// <see cref="RouterDifficulty.Easy"/> on shape alone, absent any other signal.</summary>
    private const int EasyPromptLengthCeiling = 200;

    private static readonly string[] UtilityPromptSignals =
    [
        "generate a title", "generate a short", "one-line summary", "summarize this conversation",
        "commit message", "chat title", "intent detection",
    ];

    private static readonly string[] HardPromptSignals =
    [
        "edge case", "edge-case", "complex", "multi-step", "multi step", "large-scale", "large scale",
        "performance-critical", "performance critical", "concurrency", "concurrent", "race condition",
        "distributed system", "thread-safe", "thread safe",
    ];

    private static readonly Regex FenceLanguageHint = new(@"```[ \t]*([A-Za-z0-9_+#.-]+)", RegexOptions.Compiled);

    private readonly IDimensionInferrer _dimensionInferrer;

    /// <summary>Initializes a new instance of the <see cref="HeuristicRequestClassifier"/> class.</summary>
    /// <param name="dimensionInferrer">
    /// The shared dimension inferrer - defaults to a fresh <see cref="TotallyHot.ArcRouter.Sandbox.Extraction.KeywordDimensionInferrer"/>
    /// when omitted, the same default <see cref="TotallyHot.ArcRouter.Proxy.RequestInterceptor"/> uses, so the two stay
    /// classified identically without either side hand-picking an instance.
    /// </param>
    public HeuristicRequestClassifier(IDimensionInferrer? dimensionInferrer = null)
    {
        _dimensionInferrer = dimensionInferrer ?? new TotallyHot.ArcRouter.Sandbox.Extraction.KeywordDimensionInferrer();
    }

    /// <inheritdoc />
    public RequestClassification Classify(JsonObject requestBody)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        var prompt = RequestTextExtractor.ExtractNewestUserMessage(requestBody) ?? string.Empty;
        var language = InferLanguage(prompt);
        var dimension = _dimensionInferrer.Infer(prompt, language);
        var difficulty = InferDifficulty(prompt);
        var isUtility = InferIsUtility(requestBody, prompt);

        return new RequestClassification(dimension, difficulty, LanguageName(language), isUtility);
    }

    /// <summary>Detects the language of the first recognized fenced code block in the prompt, if any.</summary>
    private static SandboxLanguage InferLanguage(string prompt)
    {
        var match = FenceLanguageHint.Match(prompt);
        return match.Success ? SandboxLanguages.FromHint(match.Groups[1].Value) : SandboxLanguage.Unknown;
    }

    private static string LanguageName(SandboxLanguage language) => language switch
    {
        SandboxLanguage.CSharp => "csharp",
        SandboxLanguage.Python => "python",
        SandboxLanguage.JavaScript => "javascript",
        SandboxLanguage.Shell => "shell",
        _ => "unknown",
    };

    /// <summary>
    /// A short prompt with no hard signal is <see cref="RouterDifficulty.Easy"/>; a long prompt or one
    /// naming a known hard-task signal (concurrency, distributed systems, and the like) is
    /// <see cref="RouterDifficulty.Hard"/>; everything else is <see cref="RouterDifficulty.Medium"/>,
    /// the tier with no strong signal either way.
    /// </summary>
    private static string InferDifficulty(string prompt)
    {
        var lower = prompt.ToLowerInvariant();

        if (prompt.Length >= HardPromptLengthFloor || ContainsAny(lower, HardPromptSignals))
        {
            return RouterDifficulty.Hard;
        }

        if (prompt.Length <= EasyPromptLengthCeiling)
        {
            return RouterDifficulty.Easy;
        }

        return RouterDifficulty.Medium;
    }

    /// <summary>
    /// <c>docs/router/utility-model-routing.md</c> B2's payload heuristics: a small <c>max_tokens</c>
    /// cap, or a short prompt whose text names a title/intent/summary-style background workflow.
    /// </summary>
    private static bool InferIsUtility(JsonObject requestBody, string prompt)
    {
        var maxTokensNode = requestBody["max_tokens"] ?? requestBody["max_completion_tokens"];
        if (maxTokensNode is JsonValue maxTokensValue &&
            maxTokensValue.TryGetValue<int>(out var maxTokens) &&
            maxTokens > 0 &&
            maxTokens <= UtilityMaxTokensCeiling)
        {
            return true;
        }

        return prompt.Length > 0 &&
            prompt.Length <= UtilityPromptLengthCeiling &&
            ContainsAny(prompt.ToLowerInvariant(), UtilityPromptSignals);
    }

    private static bool ContainsAny(string haystack, IReadOnlyList<string> needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
