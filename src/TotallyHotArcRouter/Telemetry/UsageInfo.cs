namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Token usage for one completed request, as reported by the upstream provider. This shape is always
/// <b>additive</b> (Anthropic's own convention: <see cref="CacheCreationTokens"/>/<see cref="CacheReadTokens"/>
/// are separate from <see cref="PromptTokens"/>, summed by <see cref="TotalInputTokens"/>) - a provider with
/// <b>inclusive</b> cache semantics instead (OpenAI's <c>prompt_tokens_details.cached_tokens</c> is a subset
/// of <c>prompt_tokens</c>) is normalized to this additive model at parse time, the one place that
/// normalization happens (see <c>OpenAiUsageParser.TryExtractFromUsageContainer</c>,
/// <c>docs/router/openai-format-usage-accuracy-plan.md</c> §1.1/§6.1).
/// </summary>
/// <param name="PromptTokens">
/// Prompt/input tokens billed at the standard input rate. For a cache-aware provider (Anthropic), this is
/// tokens <em>after</em> the last cache breakpoint, not the request's full input - see
/// <see cref="TotalInputTokens"/> for the true total.
/// </param>
/// <param name="CompletionTokens">Completion/output tokens.</param>
/// <param name="CacheCreationTokens">
/// Input tokens written to a new prompt cache entry (Anthropic's <c>cache_creation_input_tokens</c>).
/// Defaults to 0, which is also the correct value for a provider that has no concept of prompt caching, so
/// every existing call site and parser compiles unchanged.
/// </param>
/// <param name="CacheReadTokens">
/// Input tokens served from an existing prompt cache entry (Anthropic's <c>cache_read_input_tokens</c>).
/// Defaults to 0 for the same reason as <see cref="CacheCreationTokens"/>.
/// </param>
/// <param name="ReasoningTokens">
/// Extended-thinking / reasoning tokens, which are a <b>subset of</b> <see cref="CompletionTokens"/>, not
/// an addition to it - OpenAI (<c>completion_tokens_details.reasoning_tokens</c>) counts them inside the
/// output total. This field exists for attribution ("how much of this turn's output was thinking?"), and
/// any cost formula that adds it on top of <see cref="CompletionTokens"/> is double-counting. Contrast
/// <see cref="CacheCreationTokens"/>/<see cref="CacheReadTokens"/>, which are genuinely additive to
/// <see cref="PromptTokens"/> - the two conventions are opposite and easy to conflate. Defaults to 0, which
/// is also correct for a provider with no extended-thinking concept, or where the response didn't request
/// it. Anthropic's Messages API reports no distinct reasoning-token count in its <c>usage</c> object -
/// thinking tokens are billed inside <c>output_tokens</c> with no separate figure exposed - so this stays 0
/// for Anthropic requests, the same "not modeled, blocked on the upstream API" treatment
/// <c>docs/router/token-tracking-improvements.md</c> §5.14 gives web-search request counts.
/// </param>
public readonly record struct UsageInfo(
    int PromptTokens,
    int CompletionTokens,
    int CacheCreationTokens = 0,
    int CacheReadTokens = 0,
    int ReasoningTokens = 0)
{
    /// <summary>
    /// Gets the true total input token count: <see cref="PromptTokens"/> plus <see cref="CacheCreationTokens"/>
    /// plus <see cref="CacheReadTokens"/>. This is the only place this formula is defined - Anthropic's
    /// <c>input_tokens</c> field counts only tokens after the last cache breakpoint, so summing the three
    /// components is required to get the full input size a request actually carried.
    /// </summary>
    public int TotalInputTokens => PromptTokens + CacheCreationTokens + CacheReadTokens;
}