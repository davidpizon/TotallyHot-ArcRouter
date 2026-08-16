using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router.TextGeneration;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The <c>llm_router</c> voter (PLAN.md Phase L): prompts a local, ONNX-hosted, off-the-shelf small
/// instruct model (<see cref="ITextGenerationClient"/>) with a zero-shot routing prompt built from
/// <see cref="VotingContext.Dimension"/>, <see cref="VotingContext.Candidates"/>, and
/// <see cref="VotingContext.TaskText"/>, then parses its response through research-doc §B.3's
/// documented fallback chain (JSON → fenced code block → model-name match) to a candidate pick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Documented substitute, not the paper's voter.</b> Research-doc §3.3/A.1 specifies this voter as
/// the paper's own <em>fine-tuned</em> Qwen3.5-0.8B - a private artifact of the paper that was never
/// published anywhere. This implementation prompts a small, off-the-shelf instruct model instead (no
/// fine-tuning), agreed with the user ahead of implementation as the plan's own documented escape
/// hatch for exactly this situation. It will not reproduce the paper's reported accuracy for this
/// voter, and is not intended to.
/// </para>
/// <para>
/// <b>Further scope narrowing versus §B.3, also deliberate:</b> zero-shot prompting only - no few-shot
/// examples and no "+Perf Stats" ablation variant, both of which need oracle-labeled example curation
/// from CodeRouterBench, a materially separate project from prompting a model. No disagreement-gated
/// invocation - this voter runs on every decision like the other three, gated only by
/// <see cref="RoutingOptions.EnableLlmRouterVoter"/>/<see cref="RoutingOptions.LlmRouterVoterWeight"/>,
/// since none of the other voters gate on ensemble disagreement either. The paper's per-voter
/// hardcoded fallback (<c>claude-sonnet-4-6</c>) becomes an abstention here instead of step 4 of the
/// parsing chain - a hardcoded model that might not even be a live candidate would be actively wrong,
/// and <see cref="OrchestratorRoutingPolicy"/> already owns the ensemble-level fallback
/// (<see cref="RoutingOptions.DefaultModel"/>).
/// </para>
/// <para>
/// Confidence is synthesized per successful parse stage (direct JSON highest, then the fenced-block
/// regex, then bare name-matching lowest) rather than paper-derived - a documented implementation
/// choice, the same way <see cref="RoutingOptions.DimBestVoterWeight"/>'s default voter weights are.
/// </para>
/// </remarks>
public sealed class LlmRouterVoter : IRoutingVoter
{
    private const double JsonParseConfidence = 0.9;
    private const double CodeBlockParseConfidence = 0.75;
    private const double NameMatchConfidence = 0.5;

    // The body excludes any run of three backticks, so a plain-greedy `.*}` can't backtrack past this
    // block's own closing fence into a later fenced block (which would splice two blocks' text together
    // into invalid JSON). Within that bound it still matches greedily, so a "reasoning" string containing
    // its own '}' or a nested object no longer truncates the capture the way a naive non-greedy body did.
    private static readonly Regex FencedCodeBlockPattern = new(
        @"```(?:json)?\s*(?<body>\{(?:[^`]|`(?!``))*\})\s*```",
        RegexOptions.Singleline | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private readonly ITextGenerationClient _generationClient;
    private readonly ILogger<LlmRouterVoter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmRouterVoter"/> class.
    /// </summary>
    /// <param name="generationClient">Runs the local instruct model this voter prompts.</param>
    /// <param name="logger">The logger.</param>
    public LlmRouterVoter(ITextGenerationClient generationClient, ILogger<LlmRouterVoter> logger)
    {
        ArgumentNullException.ThrowIfNull(generationClient);
        ArgumentNullException.ThrowIfNull(logger);

        _generationClient = generationClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => VoterNames.LlmRouter;

    /// <inheritdoc />
    /// <remarks>
    /// Abstains without task text to route on (nothing to prompt the model with), when the model's
    /// response fails every stage of the parsing chain, when it names a model outside
    /// <see cref="VotingContext.Candidates"/>, or when the generation client throws or times out - this
    /// voter never lets an unavailable or misbehaving model fail the whole routing decision, per
    /// <see cref="IRoutingVoter"/>'s contract.
    /// </remarks>
    public async Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.TaskText))
        {
            return VoterVote.Abstain(Name);
        }

        string response;
        try
        {
            var prompt = BuildPrompt(context.Dimension, context.Candidates, context.TaskText);
            response = await _generationClient.GenerateAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The generation client's own timeout fired, not the caller's token - degrade to an
            // abstention per this method's contract instead of propagating a timeout as a failure.
            _logger.LogWarning("[LLM_ROUTER] Generation timed out; abstaining.");
            return VoterVote.Abstain(Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[LLM_ROUTER] Generation failed; abstaining.");
            return VoterVote.Abstain(Name);
        }

        try
        {
            return ParseResponse(response, context.Candidates);
        }
        catch (RegexMatchTimeoutException ex)
        {
            // The fenced-code-block regex hit its 200ms timeout - degrade to an abstention like any
            // other unparseable response, per this method's contract, rather than letting a
            // pathological response string fail the whole routing decision.
            _logger.LogWarning(ex, "[LLM_ROUTER] Response parsing timed out; abstaining.");
            return VoterVote.Abstain(Name);
        }
    }

    /// <summary>
    /// Builds research-doc §B.3's zero-shot routing prompt, ChatML-formatted for Qwen2.5-Instruct's
    /// native chat template (hand-built rather than depending on an uncertain
    /// <c>ApplyChatTemplate</c>-style API surface in the generation client).
    /// </summary>
    private static string BuildPrompt(string dimension, IReadOnlyList<RoutingCandidate> candidates, string taskText)
    {
        var modelList = string.Join(", ", candidates.Select(c => c.ModelName));

        var system =
            "You are a coding task router. Your objective is to maximize the performance-cost " +
            "trade-off: choose the model that achieves the best quality for its cost on this task. " +
            $"Candidate models: {modelList}. Prefer cheaper models when quality is comparable. " +
            "Respond with JSON: {\"model\": \"...\", \"reasoning\": \"...\"}.";

        var user = $"Dimension: {dimension}\nTask: {taskText}";

        return $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n";
    }

    /// <summary>
    /// Research-doc §B.3's response-parsing fallback chain, narrowed to three stages (see the type-level
    /// remarks for why step 4 is an abstention here rather than a hardcoded model).
    /// </summary>
    private VoterVote ParseResponse(string response, IReadOnlyList<RoutingCandidate> candidates)
    {
        if (TryParseJsonModel(response, out var direct) && TryMatchCandidate(direct, candidates, out var directMatch))
        {
            return new VoterVote(Name, directMatch, JsonParseConfidence);
        }

        var codeBlockMatch = FencedCodeBlockPattern.Match(response);
        if (codeBlockMatch.Success &&
            TryParseJsonModel(codeBlockMatch.Groups["body"].Value, out var fenced) &&
            TryMatchCandidate(fenced, candidates, out var fencedMatch))
        {
            return new VoterVote(Name, fencedMatch, CodeBlockParseConfidence);
        }

        // When the response mentions more than one candidate (common when it restates the candidate
        // list before committing to one), the last-mentioned name is taken as the pick - it tracks the
        // response's own content instead of candidates' fixed list order, which would otherwise silently
        // prefer whichever candidate happens to sort first regardless of what the model actually said.
        // Ties (same start index) happen when one candidate's name is a prefix of another's, e.g.
        // "llama3" vs "llama3-70b-bedrock": a response containing only the longer name still matches
        // "llama3" at that same position. Break such ties toward the longer name, since it is the more
        // specific (and only fully-supported-by-the-text) match.
        string? lastMentioned = null;
        var lastMentionedIndex = -1;
        foreach (var candidate in candidates)
        {
            var index = response.LastIndexOf(candidate.ModelName, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            if (index > lastMentionedIndex ||
                (index == lastMentionedIndex && lastMentioned is not null && candidate.ModelName.Length > lastMentioned.Length))
            {
                lastMentionedIndex = index;
                lastMentioned = candidate.ModelName;
            }
        }

        if (lastMentioned is not null)
        {
            return new VoterVote(Name, lastMentioned, NameMatchConfidence);
        }

        _logger.LogInformation("[LLM_ROUTER] Could not parse a candidate model from the generated response; abstaining.");
        return VoterVote.Abstain(Name);
    }

    private static bool TryParseJsonModel(string text, out string? modelName)
    {
        modelName = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("model", out var modelProperty) &&
                modelProperty.ValueKind == JsonValueKind.String)
            {
                modelName = modelProperty.GetString();
                return !string.IsNullOrWhiteSpace(modelName);
            }
        }
        catch (JsonException)
        {
            // Not valid JSON at this stage - the caller falls through to the next parse stage.
        }

        return false;
    }

    /// <summary>
    /// Matches a parsed model name against the live candidate set, tolerating the same cosmetic
    /// spelling differences <see cref="OrchestratorRoutingPolicy"/> itself tolerates when reconciling a
    /// voter's pick.
    /// </summary>
    private static bool TryMatchCandidate(string? modelName, IReadOnlyList<RoutingCandidate> candidates, out string matchedModelName)
    {
        matchedModelName = string.Empty;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        var match = candidates.FirstOrDefault(candidate =>
            string.Equals(
                ModelNameCanonicalizer.Canonicalize(candidate.ModelName, candidate.Provider),
                ModelNameCanonicalizer.Canonicalize(modelName, candidate.Provider),
                StringComparison.Ordinal));

        if (match is null)
        {
            return false;
        }

        matchedModelName = match.ModelName;
        return true;
    }
}
