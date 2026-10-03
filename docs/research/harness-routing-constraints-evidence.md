# Evidence: what constrains routing harness traffic across backends

**Researched:** 2026-10-02, for [ADR-0017](../adr/0017-pin-auto-routed-requests-only-when-they-carry-backend-specific-features.md).
**Used by:** ADR-0017 (revised 2026-10-02) and
[tracked TODO #8](../router/tracked-todos.md#8-capture-and-analyze-real-claude-code-and-codex-traffic-before-deciding-adr-0017s-pin-policy),
the traffic census that decides ADR-0017's policy table.
**Companion:** [`subagent-and-helper-routing-evidence.md`](subagent-and-helper-routing-evidence.md) covers which model a
subagent or helper request should get. This file covers which backends a request *can* go to, and what moving a
conversation between them costs.

The question: when Arc Router auto-routes a Claude Code, Codex or Copilot request, which candidates can serve it, which
of its features can be removed safely, and when is it worth moving a conversation to a different model?

Everything below is paraphrased from the linked pages, read on the date above. Vendor documentation changes often;
re-check a source before relying on a detail that matters. Nothing here was verified by capturing live traffic through
the router. That is the census's job.

Each finding carries a confidence tag:

- **[Doc]** — read directly on the vendor's own documentation page.
- **[Doc, excerpt]** — taken from a search-result excerpt of a vendor page, not read in full.
- **[3P]** — a third-party report (issue tracker, blog). Plausible, not authoritative.
- **[Code]** — verified in this repository on the date above.
- **[Inferred]** — reasoned from the findings above it; not stated by any source.

## 1. What the harnesses send

### 1.1 Claude Code behind `ANTHROPIC_BASE_URL`

Source: [gateway compatibility guide](https://code.claude.com/docs/en/llm-gateway-protocol) unless noted.

- **[Doc]** Claude Code treats an `ANTHROPIC_BASE_URL` gateway as the Claude API. It cannot tell which upstream the
  gateway forwards to, so it sends its full set of beta headers and request body fields.
- **[Doc]** For a model id it does not recognize, such as a gateway alias or Arc Router's `auto`, Claude Code assumes a
  current Claude model. It sends adaptive thinking (`thinking: {"type": "adaptive"}`), effort (`output_config.effort`)
  and context management (`context_management`). The guide warns that a Bedrock or Vertex upstream can reject these.
- **[Doc]** For an unrecognized id, Claude Code assumes a 200K context window (1M if the id carries `[1m]`). A
  `modelOverrides` entry can give an alias a real model's capabilities. The
  `ANTHROPIC_DEFAULT_*_MODEL_SUPPORTED_CAPABILITIES` variables have no effect behind an `ANTHROPIC_BASE_URL` gateway.
- **[Doc]** A capability that adds a body field pairs it with an `anthropic-beta` value. Stripping one half and
  forwarding the other gives a hard 400. Only removing both halves turns the feature off quietly.
- **[Doc]** The guide tells gateways to forward `anthropic-*` headers and body fields as open lists, because each release
  adds new ones. A gateway that allowlists today's fields breaks the next release's capability.
- **[Doc]** Claude Code has its own recovery for some upstream rejections:
  - A rejected `thinking` field, mid-conversation system message, or `cache_control` on such a message: it retries and
    disables that capability for the rest of the conversation.
  - A rejected thinking signature, including "bound to a different conversation": it removes earlier thinking blocks,
    retries, and keeps them out of every later request.
  - A rejected `output_config.effort`: it retries without effort for that model until it exits.
  - Context-management and tool-schema rejections are not retried. The 400 reaches the developer.
  - Recovery matches on the upstream's error wording, so a gateway must forward error bodies unmodified.
- **[Doc]** `CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS=1` stops pre-release capabilities: `context_management`, beta tool
  fields (`strict`, `defer_loading`), `output_config.format`, `output_config.task_budget`, and MCP tool search. It keeps
  the extended-context, interleaved-thinking and effort beta values, `output_config.effort`, adaptive thinking and the
  OAuth beta value.
- **[Doc]** Headers: `x-claude-code-session-id` is sent on every request. `x-claude-code-agent-id` and
  `x-claude-code-parent-agent-id` mark subagent requests. Behind a custom base URL, the hint headers
  (`x-claude-code-request-class`, `-agent-type`, `-compaction`, `-context-compacted`, `-prev-tool-durations`,
  `-prompt-id`) are sent only with `CLAUDE_CODE_GATEWAY_HINT_HEADERS=1`. `x-claude-code-context-compacted` marks the
  first request after a compaction, whose old prefix is no longer used.
- **[Doc]** Claude Code attaches `cache_control` to system blocks and messages. A gateway that strips the markers and
  returns success makes the whole history bill as uncached input on every turn. A gateway that rejects them with a 400
  makes Claude Code move the marker to the last message.
  ([How Claude Code uses prompt caching](https://code.claude.com/docs/en/prompt-caching))
- **[Doc]** Through a gateway with an API-key-style credential, the main conversation's cache TTL defaults to five
  minutes. A one-hour TTL is opt-in (`promptCacheTtl`) and needs the `extended-cache-ttl` beta value forwarded.
  ([prompt caching](https://code.claude.com/docs/en/prompt-caching))
- **[Doc]** Anthropic does not support routing Claude Code to non-Claude models through any gateway.
  ([Other LLM gateways](https://code.claude.com/docs/en/llm-gateway))

### 1.2 Codex

- **[Doc]** `wire_api` accepts only `responses`. Codex always speaks the Responses API to a custom provider.
  ([config reference](https://learn.chatgpt.com/docs/config-file/config-reference))
- **[Doc]** `model_catalog_json`, `model_supports_reasoning_summaries` and `model_context_window` let a user describe a
  model Codex does not know. ([config reference](https://learn.chatgpt.com/docs/config-file/config-reference))
- **[3P]** For a model id missing from its catalogue, Codex falls back to conservative defaults, including a smaller
  context window than the model may support.
  ([Codex model catalogue write-up](https://codex.danielvaughan.com/2026/05/04/codex-cli-model-catalogue-architecture-providers-discovery-debug/))
  So `model = "auto"` may change what Codex sends. The census's two-configuration capture exists for this.
- **[Doc, excerpt]** In stateless mode (`store: false`, or a zero-data-retention organization), reasoning items carry
  `encrypted_content`, which the client replays on later requests.
  ([OpenAI reasoning guide](https://developers.openai.com/api/docs/guides/reasoning))
- **[3P]** That encrypted content can be decrypted only by the organization that produced it. Replaying it to a different
  upstream fails with a 400 (`invalid_encrypted_content`), and the thread does not recover.
  ([getpaseo/paseo#4542](https://github.com/getpaseo/paseo/issues/4542))

### 1.3 GitHub Copilot

- **[Doc]** Copilot CLI's bring-your-own-key mode takes `COPILOT_PROVIDER_TYPE` (`openai`, `azure` or `anthropic`) and
  `COPILOT_PROVIDER_WIRE_API`. `COPILOT_PROVIDER_MODEL_ID` names the model whose capabilities and limits to assume,
  separately from `COPILOT_PROVIDER_WIRE_MODEL`, the id sent upstream. Models must support tool calling and streaming;
  128K context or more is recommended.
  ([Copilot CLI BYOK](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models))
- **[Doc]** The Copilot SDK's `wireApi` is `completions` (Chat Completions, the default) or `responses`. The `anthropic`
  type always uses the Messages API. ([Copilot SDK BYOK](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/byok))
- **[Inferred]** Copilot's dialect is a configuration choice. Chat Completions is the router's own translation format
  ([`unified-api-translation.md`](../router/unified-api-translation.md)), so a Copilot preset on Chat Completions gets
  the widest routing today.

## 2. What the backends accept

### 2.1 Anthropic: thinking

- **[Doc]** Manual extended thinking (`type: "enabled"` with `budget_tokens`) returns a 400 on Claude 4.7 and later.
  Adaptive thinking returns a 400 on models that support only extended thinking: Claude Sonnet 4.5, Opus 4.5,
  Haiku 4.5 and earlier Claude 4 models.
  ([Extended thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking))
- **[Doc]** During tool use, the thinking blocks of the current assistant turn must be passed back unmodified. Thinking
  cannot be toggled mid-turn. A mid-turn toggle does not error: the API quietly disables thinking for that request.
  Manual mode also requires the final assistant turn to begin with a thinking block; adaptive mode drops that rule.
  ([Thinking](https://platform.claude.com/docs/en/build-with-claude/thinking))
- **[Doc]** A thinking block is readable only by the model that produced it and certain other models. When a
  conversation moves to a model that cannot read a block, the API drops the block without an error and without billing
  it. ([Thinking](https://platform.claude.com/docs/en/build-with-claude/thinking),
  [Preserved thinking](https://platform.claude.com/docs/en/build-with-claude/preserved-thinking))
- **[Doc]** From Claude Fable 5.1, Opus 5.5 and Sonnet 5.5, the API checks that the `system` prompt, the `tools` and
  every earlier message are unchanged since the block was produced. If not, the request fails with a 400 saying the
  block is bound to a different conversation, unless a beta setting asks the API to drop such blocks instead.
  `cache_control` markers and request parameters such as effort and `max_tokens` do not count as changes.
  ([Preserved thinking](https://platform.claude.com/docs/en/build-with-claude/preserved-thinking))
- **[Doc]** Removing all thinking blocks, or removing them from the start or the end of the history, is valid. Removing
  one from the middle while keeping later ones invalidates every later block.
  ([Preserved thinking](https://platform.claude.com/docs/en/build-with-claude/preserved-thinking))
- **[Doc]** Claude Sonnet 5.5 thinking blocks are bound to the account that produced them. Another account's request
  carrying them succeeds, but the blocks are dropped first.
  ([Preserved thinking](https://platform.claude.com/docs/en/build-with-claude/preserved-thinking))
- **[Doc]** Thinking signatures work across the Claude API, Amazon Bedrock and Google Cloud.
  ([Thinking](https://platform.claude.com/docs/en/build-with-claude/thinking))

### 2.2 Anthropic: prompt caching and prices

- **[Doc]** Each model has its own cache. Switching models re-reads the whole conversation uncached, even when the
  content is identical. ([How Claude Code uses prompt caching](https://code.claude.com/docs/en/prompt-caching))
- **[Doc]** Caches are isolated per organization, and per workspace on the Claude API.
  ([Prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching))
- **[Doc]** A hit needs an exact prefix match. On each request the API looks back up to 20 block positions before a
  breakpoint for an entry an earlier request wrote. A run of `tool_use` or `tool_result` blocks counts as one
  position. ([Prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching))
- **[Doc]** Changing tool definitions invalidates the whole cache. Changing the thinking configuration or the effort
  level always invalidates message-level breakpoints.
  ([Prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching),
  [Thinking](https://platform.claude.com/docs/en/build-with-claude/thinking))
- **[Doc]** Price multipliers on the base input price: a 5-minute cache write costs 1.25×, a 1-hour write 2×, and a
  cache read 0.1×. Exceptions: reads cost 0.05× on Claude Opus 5.5, and 0.025× on Claude Fable 5.1 and Mythos 5.1.
  ([Pricing](https://platform.claude.com/docs/en/about-claude/pricing))
- **[Doc]** Prices used in §4, in USD per million tokens ([Pricing](https://platform.claude.com/docs/en/about-claude/pricing)):

  | Model | Input | 5-min write | Cache read | Output |
  |---|---|---|---|---|
  | Claude Fable 5.1 | 10.00 | 12.50 | 0.25 | 50.00 |
  | Claude Opus 5.5 | 4.00 | 5.00 | 0.20 | 20.00 |
  | Claude Sonnet 5.5 | 2.00 | 2.50 | 0.20 | 10.00 |
  | Claude Haiku 4.5 | 1.00 | 1.25 | 0.10 | 5.00 |

- **[Doc]** Claude 4.7 and later use a newer tokenizer that produces about 30% more tokens for the same text.
  ([Pricing](https://platform.claude.com/docs/en/about-claude/pricing))

### 2.3 Other endpoints that speak a harness's dialect

- **[Doc]** LM Studio 0.4.1 (2026-01-30) serves an Anthropic-compatible `POST /v1/messages`, aimed at Claude Code. Its
  docs cover messages, streaming and tool use. They do not mention thinking, images, `cache_control` or token counting.
  ([LM Studio blog](https://lmstudio.ai/blog/claudecode),
  [Messages docs](https://lmstudio.ai/docs/developer/anthropic-compat/messages))
- **[Doc]** LM Studio also serves `POST /v1/responses` with streaming, reasoning effort, `previous_response_id` and
  optional remote MCP. ([Responses docs](https://lmstudio.ai/docs/developer/openai-compat/responses))
- **[Doc]** Ollama 0.14 serves `/v1/messages`. It supports text, image, tool and thinking blocks, `system`, `stream`,
  sampling parameters, `tools`, `thinking` and `output_config.effort`. It does not support `count_tokens`, `tool_choice`,
  `metadata`, `cache_control`, batches or PDF document blocks. The docs do not say whether an unsupported field is
  ignored or rejected. ([Ollama Anthropic compatibility](https://docs.ollama.com/api/anthropic-compatibility))
- **[Doc]** Bedrock and Google Cloud upstreams reject `context_management` and `output_config` that arrive in Messages
  format ("Extra inputs are not permitted"). ([gateway compatibility guide](https://code.claude.com/docs/en/llm-gateway-protocol))

## 3. What the router has today

All **[Code]**, checked 2026-10-02 on `feature/163-subagent-aware-routing`.

- `RoutingCandidateBuilder.GetEligibleRoutes` (`src/TotallyHotArcRouter/Proxy/RoutingCandidateBuilder.cs:226`) filters
  only on circuit state and operator stop/disable. Its two production callers are `RankEligibleModels` (failover) and
  `RequestInterceptor.BuildRoutingCandidates` (the policy's menu).
- `AnthropicPayloadTranslator.ShouldTranslate` skips `/v1/messages`, so a native Messages body reaches the `anthropic`
  provider untranslated. Other translators, including `AnthropicOnBedrockPayloadTranslator` (whose `ShouldTranslate`
  always returns true), treat a Messages body as Chat Completions. `/v1/responses` is forwarded untranslated
  ([`codex.md`](../install/harnesses/codex.md), [`claude-code.md`](../install/harnesses/claude-code.md)).
- `ProviderEndpointCapabilities` (`src/TotallyHotArcRouter/Proxy/Translation/ToolCalling/ToolCallCapabilities.cs`) has
  `OpenAiCompatible`, `LmStudioNative`, `OllamaNative`, `AnthropicCompatible` and `JsonSchemaResponseFormat`. There is
  no Responses flag. `ProviderOptions.ProbeAnthropicMessages` opts a provider into a POST probe with a sentinel model id.
- `JsonSchemaResponseFormat` is inferred from the detected server type rather than probed, because a real probe would
  make a local server load a model. `DetectionConfidence` orders evidence as Heuristic < Template < Observed < Operator,
  and an operator-set value is never overwritten.
- No vision or document capability is recorded anywhere in `src/TotallyHotArcRouter`.
- Commit 3a690a1 (#163, ADR-0022) restricts biased Claude Code traffic on `/v1/messages` to candidates whose provider
  key is `anthropic` (`RequestInterceptor.IsNativeMessagesProvider`). It does not consult `AnthropicCompatible`.
- The default `src/TotallyHotArcRouter/appsettings.json` ships `claude-haiku-4-5-20251001` under the `anthropic`
  provider (line 186).
- `AnthropicUsageParser` reads `cache_read_input_tokens` and `OpenAiUsageParser` reads `cached_tokens`. The usage tables
  carry `cache_creation_tokens` and `cache_read_tokens`. The price catalog carries `CacheReadPerMillionTokens` and
  `CacheWritePerMillionTokens`, and `PriceContext.RepeatsCachedContext` already prices a cached prefix.
- `SessionIdResolver` reads `x-claude-code-session-id`. `MessageHistoryContinuityMatcher` fingerprints the `messages`
  array only, so it cannot key Responses traffic.

## 4. The cost of moving a conversation

### 4.1 Model

Take a conversation on model **A**. The router is considering model **B** for the next request. Per-model quantities,
in dollars per token:

- *p* — base input price; *q* — output price.
- *r* — cache-read multiplier (0.1, or 0.05 on Opus 5.5, or 0.025 on Fable 5.1).
- *w* — cache-write multiplier (1.25 for the 5-minute TTL, 2 for one hour; 1 for a provider with no write premium).

Per-request quantities, in tokens:

- *C* — the conversation prefix that is cached on A when the request arrives.
- *n* — new input tokens in this request. *o* — output tokens.

The prefix is identical whichever model reads it, but **each model has its own cache** (§2.2). So:

$$c_{\text{warm}}(X) = r_X\,p_X\,C + w\,p_X\,n + q_X\,o \qquad \text{(X already cached the prefix)}$$

$$c_{\text{cold}}(X) = w\,p_X\,(C+n) + q_X\,o \qquad \text{(X writes the whole prefix fresh)}$$

### 4.2 Single-request break-even

Moving to B pays on the first request only if $c_{\text{cold}}(B) < c_{\text{warm}}(A)$. With a large prefix
($C \gg n$) and similar output, that reduces to:

$$w\,p_B < r_A\,p_A \quad\Longleftrightarrow\quad \frac{p_B}{p_A} < \frac{r_A}{w}$$

| A's cache-read multiplier *r_A* | 5-minute TTL (*w* = 1.25) | 1-hour TTL (*w* = 2) |
|---|---|---|
| 0.1 (most models) | B ≥ 12.5× cheaper | B ≥ 20× cheaper |
| 0.05 (Opus 5.5) | B ≥ 25× cheaper | B ≥ 40× cheaper |
| 0.025 (Fable 5.1) | B ≥ 50× cheaper | B ≥ 80× cheaper |

Example: Opus 5.5 → Haiku 4.5 with *C* = 100,000 tokens. Staying reads the prefix for 100,000 × \$0.20/M = **\$0.020**.
Moving writes it for 100,000 × \$1.25/M = **\$0.125**, which is 6.25× more for that request, although Haiku's list price
is a quarter of Opus's.

### 4.3 Multi-request break-even

A move pays off if the conversation stays on B long enough. Over *k* requests, B is cheaper when

$$c_{\text{cold}}(B) + (k-1)\,c_{\text{warm}}(B) < k\,c_{\text{warm}}(A)$$

Write $\Delta = c_{\text{cold}}(B) - c_{\text{warm}}(A)$ for the extra cost of the move, and
$s = c_{\text{warm}}(A) - c_{\text{warm}}(B)$ for the saving on each later request. When *s* > 0:

$$k > 1 + \frac{\Delta}{s}$$

Holding *C* constant is conservative only when $r_A p_A > r_B p_B$, because the per-request saving then grows as the
prefix grows.

### 4.4 Worked examples

The token counts are assumptions for illustration, not measurements: *C* = 100,000, *n* = 2,000, *o* = 1,000, and
5-minute TTL. The census replaces them. Prices are from §2.2.

| Move | *c*_warm(A) | *c*_warm(B) | *c*_cold(B) | Δ | *s* | Requests on B to break even |
|---|---|---|---|---|---|---|
| Opus 5.5 → Haiku 4.5 | \$0.0500 | \$0.0175 | \$0.1325 | \$0.0825 | \$0.0325 | *k* > 3.54 → **4** |
| Sonnet 5.5 → Haiku 4.5 | \$0.0350 | \$0.0175 | \$0.1325 | \$0.0975 | \$0.0175 | *k* > 6.57 → **7** |
| Opus 5.5 → Sonnet 5.5 | \$0.0500 | \$0.0350 | \$0.2650 | \$0.2150 | \$0.0150 | *k* > 15.3 → **16** |

How each cell is computed, for Opus 5.5 → Haiku 4.5 (prices in \$/M tokens, tokens in millions):

- *c*_warm(Opus 5.5) = 0.05·4·0.1 + 1.25·4·0.002 + 20·0.001 = 0.020 + 0.010 + 0.020 = 0.0500
- *c*_warm(Haiku 4.5) = 0.1·1·0.1 + 1.25·1·0.002 + 5·0.001 = 0.010 + 0.0025 + 0.005 = 0.0175
- *c*_cold(Haiku 4.5) = 1.25·1·0.102 + 5·0.001 = 0.1275 + 0.005 = 0.1325

Two things stand out:

- **Output prices drive the payback.** Counting input alone, Opus 5.5 → Haiku 4.5 needs
  $k > 1 + (1.25 - 0.2)/(0.2 - 0.1) = 11.5$, so 12 requests. Output savings cut that to 4.
- **Opus 5.5 → Sonnet 5.5 barely pays.** Opus 5.5's 0.05× read rate makes its cached input cost \$0.20/M, the same as
  Sonnet 5.5's. Halving the list price saves only on new input and output, so the move needs 16 requests on Sonnet.

### 4.5 When moving is free or cheap

The penalty Δ assumes A's cache is warm and B's is cold. It shrinks or vanishes when:

- **The conversation starts.** Nothing is cached anywhere. A Claude Code subagent starts its own conversation with its
  own cache (§1.1), so its first request is also free to route.
- **A's cache has expired.** After the TTL with no request (5 minutes by default through a gateway, §1.1), staying costs
  $c_{\text{cold}}(A)$ too. Then $\Delta = c_{\text{cold}}(B) - c_{\text{cold}}(A)$, which is negative whenever B is
  cheaper.
- **The conversation was compacted.** The old prefix is discarded, so *C* drops to the summary.
  `x-claude-code-context-compacted` marks this request when hint headers are on.
- **B is still warm for this conversation.** If B served this conversation within its TTL, and that entry is within the
  20-position lookback, B reads its own earlier prefix. Only the content added since B's last request is written cold.
  So a back-and-forth inside the TTL pays the full penalty once per model, not on every swap. Each model's last-served
  time and prefix length per conversation can be tracked from the usage the router already parses (§3).

### 4.6 Caveats

- **Tokenizers differ.** Claude 4.7 and later count about 30% more tokens for the same text (§2.2), so *C* on B is not
  *C* on A. Use B's own count when known, or scale by the tokenizer ratio.
- **Free and local providers.** For a provider configured as free (`ProviderOptions.IsFree`), *p* = 0 and the money
  penalty vanishes. Re-reading a 100,000-token prefix on a local runtime still costs time to the first token. This model
  does not price latency.
- **Quality is out of scope.** The inequalities say when a move is cheaper, not when it is better. A move the routing
  policy wants for quality costs Δ, and §4.3 says how many requests it takes to earn that back.

## 5. Why probes cannot establish feature support

- **[Code]** Testing whether a feature is honored needs a generation request naming a real model. On a local runtime
  that loads the model. The router already declines to do this for `JsonSchemaResponseFormat`.
- **[Doc]** Some backends ignore rather than reject. Ollama documents `cache_control` as unsupported, but not whether it
  rejects or ignores the field. An ignored marker returns success, so a single request cannot tell. Spotting it takes a
  repeated prefix and a look at `cache_read_input_tokens`.
- **[Doc]** Some facts belong to a model, not an endpoint. On the same Anthropic endpoint, Opus 4.6 accepts adaptive
  thinking and Haiku 4.5 returns a 400 (§2.1).
- **[Inferred]** So feature support is best kept per provider and model, filled in three ways, in increasing order of
  trust: seeded defaults from the detected server type and its documentation; observations from live traffic (a 400
  that names a field, or a cache marker that never produces reads); and the operator's override. That mirrors
  `DetectionConfidence`.

## 6. Implications for ADR-0017

| Finding | ADR-0017 before this research | Revision |
|---|---|---|
| LM Studio and Ollama speak Messages; LM Studio speaks Responses; Copilot can speak any of the three (§1.3, §2.3) | Option 1 rejected as "never reaches a model outside the vendor's dialect" | Dialect is matched against endpoint capability, not provider key. A Responses capability is added. |
| `auto` gets adaptive thinking, effort and context management; 4.5-generation Claude and Bedrock reject them (§1.1, §2.1, §2.3) | Capabilities per dialect | Capabilities per provider *and model*, per feature |
| Encrypted reasoning, `previous_response_id` and Sonnet 5.5 thinking are bound to their issuer (§1.2, §2.1, §2.3) | Pin by capability, or Strip | A fourth policy, Affinity, keyed on the issuing provider |
| Adaptive mode does not reject a turn without thinking blocks; unreadable blocks are dropped silently; the hard error is the prefix check (§2.1) | "Anthropic rejects a tool-loop turn without them" | Thinking moves from Pin to soft Affinity plus Strip. Strip never alters a history bound for its issuer, and translators never invent thinking blocks. |
| Each model has its own cache; §4 prices a move (§2.2, §4) | Stickiness optional, "decided by the census" | Soft affinity with a switch-cost test is part of the decision |
| Beta header and body field travel as pairs; Claude Code's recovery reads error wording (§1.1) | Not addressed | Strip removes pairs together. Native-path error bodies are forwarded verbatim. |
| No Responses, vision or hosted-tool capability exists in code (§3) | Listed as if the data existed | Each table row names its data source, or "none yet" |
| Anthropic does not support Claude Code on non-Claude models; Claude Code adds fields each release (§1.1) | Inbound translators decided by a per-request share *P* | Translators deferred behind native-compatible routing. The rule is measured per conversation and net of switch costs. |

## 7. Open questions the census must answer

These are added to tracked TODO #8:

1. Does Claude Code send adaptive thinking, effort and `context_management` on helper, subagent and compaction requests
   when the model is `auto`? That decides whether the 3a690a1 precursor already sends a 400-bound request to Haiku 4.5.
2. What does Codex send with `model = "auto"` compared with its default model: encrypted reasoning, `store`, the
   `apply_patch` tool's type?
3. How long are conversations (requests per conversation), and how many requests remain after request *j*? That
   distribution replaces *k* in §4.3.
4. How often does a conversation sit idle longer than the 5-minute TTL? That is how often moving is free under §4.5.
5. What *C*, *n* and *o* do real requests have? Those replace the §4.4 assumptions.
6. Which 400s name which fields, per provider and model? Those seed the Observed tier.
7. Does LM Studio's or Ollama's Messages endpoint reject or ignore `cache_control`, signed thinking blocks,
   `context_management` and `output_config`?

## Sources

All read 2026-10-02.

Anthropic and Claude Code:

- [Claude Code gateway compatibility guide](https://code.claude.com/docs/en/llm-gateway-protocol)
- [Claude Code: Other LLM gateways](https://code.claude.com/docs/en/llm-gateway)
- [How Claude Code uses prompt caching](https://code.claude.com/docs/en/prompt-caching)
- [Thinking](https://platform.claude.com/docs/en/build-with-claude/thinking)
- [Extended thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking)
- [Preserved thinking](https://platform.claude.com/docs/en/build-with-claude/preserved-thinking)
- [Prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)
- [Pricing](https://platform.claude.com/docs/en/about-claude/pricing)

OpenAI and Codex:

- [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference)
- [OpenAI reasoning guide](https://developers.openai.com/api/docs/guides/reasoning) (search excerpt only)
- [getpaseo/paseo#4542: `invalid_encrypted_content` after a provider change](https://github.com/getpaseo/paseo/issues/4542) (third party)
- [Codex CLI model catalogue architecture](https://codex.danielvaughan.com/2026/05/04/codex-cli-model-catalogue-architecture-providers-discovery-debug/) (third party)

LM Studio and Ollama:

- [Use your LM Studio models in Claude Code](https://lmstudio.ai/blog/claudecode)
- [LM Studio Messages endpoint](https://lmstudio.ai/docs/developer/anthropic-compat/messages)
- [LM Studio Responses endpoint](https://lmstudio.ai/docs/developer/openai-compat/responses)
- [Ollama Anthropic compatibility](https://docs.ollama.com/api/anthropic-compatibility)

GitHub Copilot:

- [Using your own LLM models in Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models)
- [Copilot SDK: bring your own key](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/byok)
