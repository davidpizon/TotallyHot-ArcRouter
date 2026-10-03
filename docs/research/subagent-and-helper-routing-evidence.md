# Evidence: how to route subagent and helper traffic

**Researched:** 2026-10-02, for issue [#163](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/163).
**Used by:** [`docs/plans/issue-163-subagent-aware-routing.md`](../plans/issue-163-subagent-aware-routing.md) and
[ADR-0022](../adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md).
**Companion:** the per-harness signal table (which headers and model names mark a subagent or helper request)
lives in [`docs/router/utility-model-routing.md`](../router/utility-model-routing.md#subagent-and-side-task-signals).
This file covers the other question: once a request is known to be a subagent or a helper, which model it should
get.

The question came from David's definition of the goal: route subagents and helper tasks to the **best quality for
the money**. Two sources of evidence answer it: what the harness vendors themselves do with this traffic, and what
this router can actually measure about it.

Everything below is paraphrased from the linked pages, which were read on the date above. Vendor documentation
changes often; re-check a source before relying on a detail that matters. Nothing here was verified by capturing
live traffic through the router.

## 1. What the vendors do with subagent traffic

**Claude Code** ([Subagents](https://code.claude.com/docs/en/sub-agents),
[Manage costs](https://code.claude.com/docs/en/costs)):

- The built-in `Plan` and `general-purpose` subagents inherit the main conversation's model.
- The built-in `Explore` subagent (read-only file discovery and code search) also runs on the main model, or on the
  `opus` alias when the main model is a Fable model.
- `claude-code-guide` (answers questions about Claude Code itself) runs on Haiku; `statusline-setup` runs on Sonnet.
- Cheaper subagents are opt-in. The docs suggest overriding `Explore` with `model: haiku` to explore on a
  lower-cost model, and setting `model: haiku` for "simple subagent tasks". `CLAUDE_CODE_SUBAGENT_MODEL` sets a
  default, and `CLAUDE_CODE_SUBAGENT_MODEL_FORCE` applies it everywhere.
- For agent-team teammates the cost advice is Sonnet, not Haiku.

**Codex** ([Subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents)):

- A subagent inherits the parent's model and reasoning effort unless a spawn request or an `[agents]` entry sets
  one.
- OpenAI's model guidance splits by task shape: the strong model for ambiguous, multi-step work that needs
  planning, tool use and validation; the small model for fast, narrowly scoped agents doing clear, repeatable or
  high-volume work.
- The docs warn that subagent workflows use more tokens than comparable single-agent runs.

**VS Code Copilot** ([Subagents](https://code.visualstudio.com/docs/copilot/agents/subagents)):

- A subagent uses, in order, the model the main agent passes, the custom agent's `model`, Auto (only when
  `chat.subagents.defaultToAuto` is on, which is off by default and experimental), then the main model.
- A model choice above the main model's cost tier is refused, so a subagent can be cheaper than the main agent
  but never more expensive.
- Subagent requests carry no documented marker.

**Anthropic's research system**
([How we built our multi-agent research system](https://www.anthropic.com/engineering/multi-agent-research-system)):

- An Opus lead with Sonnet subagents beat single-agent Opus by 90.2% on Anthropic's internal research eval.
- Token usage explained most of the variance, with tool-call count and model choice as the other two factors.
  Moving to a newer model gained more than doubling the token budget on the older one.
- Multi-agent runs used about 15 times the tokens of a chat.

**Pattern.** All three harnesses default subagents to the main model. They move a subagent to a cheaper model only
for narrow, read-only or repetitive work, and only when someone opts in. Anthropic's own multi-agent system used a
strong mid-tier model for subagents, not the cheapest one. Subagents are also where multi-agent cost
concentrates.

## 2. What the vendors do with helper traffic

- **VS Code Copilot** ([AI language models in VS Code](https://code.visualstudio.com/docs/agent-customization/language-models)):
  `chat.utilitySmallModel` serves commit messages, pull-request titles and descriptions, rename suggestions,
  branch names, prompt categorization and intent detection, and the docs recommend a fast, inexpensive model for
  it. `chat.utilityModel` serves titles and summaries, settings search and Git review. Both default to GitHub's
  built-in utility model.
- **Claude Code** ([Manage costs](https://code.claude.com/docs/en/costs)): background work such as conversation
  summarization typically costs under $0.04 per session. Behind a gateway, background tasks use the main model
  unless `ANTHROPIC_DEFAULT_HAIKU_MODEL` pins one
  ([gateway guide](https://code.claude.com/docs/en/llm-gateway-protocol#requests-and-defaults-by-connection-method)).
- **Codex memory** ([Memories](https://learn.chatgpt.com/docs/customization/memories)): extraction and
  consolidation have separate model overrides (`memories.extract_model`, `memories.consolidation_model`). The
  official page does not state the defaults. Third-party write-ups report a mini model for extraction and the
  full model for consolidation
  ([codex-cli-best-practice](https://github.com/shanraisshan/codex-cli-best-practice/blob/main/best-practice/codex-memory.md),
  [Codex memory deep dive](https://codex.danielvaughan.com/2026/04/18/codex-built-in-memory-system-deep-dive/)).
  **Unverified against OpenAI's own docs.** If true, OpenAI treats consolidation, whose output persists into later
  sessions, as work for the stronger model.

**Pattern.** Helpers are cheap by vendor design, and they are a small share of spend.

## 3. The exception: safety classifiers

[Claude Code's permission-modes page](https://code.claude.com/docs/en/permission-modes) (auto mode, "Cost and
latency"):

- The auto-mode classifier, which decides whether an action may run without asking the user, runs on Claude
  Sonnet 5 by default rather than the session's `/model` choice. It falls back to the session's model, or to an
  Opus model, only in listed cases.
- Behind a gateway, Claude Code first asks the server to review actions inside the normal model requests. When the
  gateway drops that review, it falls back to sending its own classifier requests.

The [gateway guide](https://code.claude.com/docs/en/llm-gateway-protocol#gateway-hint-headers) lists classifiers,
alongside session titles and summaries, under `x-claude-code-request-class: auxiliary`. So the one header that
marks a helper also covers a safety decision Anthropic deliberately keeps on a strong model, and the header alone
cannot tell them apart. What can: the classifier request names a specific model (Sonnet 5 by default), while
title and summary requests carry whatever the main model is, which is `auto` under this repo's
[Claude Code preset](../install/harnesses/claude-code.settings.json).

## 4. What this router can measure

From the code, not from docs:

- **Only responses with a fenced code block are graded.** `CodeBlockSignalExtractor.Extract` returns nothing when
  a response has no fenced code block, and `QualityIngress.TryIngest` then drops it. The LLM judges are started
  downstream of the same intake (`QualityScoreAggregator`, `GraderDispatcher`).
- **Consequences.** Helper output (titles, commit messages, labels, classifier verdicts) is effectively never
  graded, so a helper quality floor never fires, and helpers write nothing into router memory. Subagent turns are
  mostly tool calls and are rarely graded. A quality floor for subagents therefore reads scores earned by graded
  traffic in the same task category, mostly main-agent code answers: a proxy for "can this model code", not for
  "can it drive tools".
- **The two routing paths.** The learned path (`OrchestratorRoutingPolicy`) ranks on quality only, explores 5% of
  the time, and records the true selection probability. The utility rule (`UtilityRoutingPolicy`) ranks on
  `ε₁·quality + ε₂·price` with `ε₁ = 1.0`, `ε₂ = −0.1` per USD per million tokens, never explores, and records a
  probability of 1.0 ([how-it-learns.md](../how-it-learns.md#so-where-does-cost-actually-come-in)).
- **The utility rule's exchange rate.** 0.1 of quality is worth $1 per million tokens. A free model at 0.50 scores
  0.50; a $3/M model at 0.95 scores 0.65; a $45/M model at 0.95 scores −3.55. Premium models almost never win on
  that rule.

## 5. How routing research frames a quality target

[RouteLLM](https://arxiv.org/abs/2406.18665) (Ong et al., 2024) routes each query between a strong and a weak model
with a learned router and states its target relative to the strong model. Its reported results include keeping
about 95% of GPT-4's MT-Bench quality at up to 3.66 times lower cost. This supports stating a quality floor as a
fraction of the best available model rather than as an absolute score. It does not supply a number for this
router: the benchmarks, models and score scale all differ.

## 6. Conclusions used by the plan

1. Helpers (Claude Code `auxiliary`, Copilot `copilot-utility*`) can take the cheap, quality-for-money rule. Every
   vendor does the same, and the router cannot grade them anyway.
2. Only bias a request when the client delegated the choice (`auto`, or a Copilot alias). That keeps the auto-mode
   classifier, which names its model, off the cheap path.
3. Read-only search subagents (Claude Code `Explore`) and the documentation helper (`claude-code-guide`, which
   Anthropic already runs on Haiku) may go cheaper, but only to a model whose known score is close to the best
   candidate's (a relative floor). With nothing qualifying, they route normally.
4. Every other subagent keeps normal, quality-first routing. That is every vendor's default, and the learned path
   already explores and records honest probabilities, so it adds no learning bias.
5. Codex `memory_consolidation` routes normally (reported strong-model default, persistent output). Codex sends no
   agent type, so its subagents cannot be split into narrow and open-ended; they route normally.
6. A cost-aware vote on the learned path, the full form of "best quality for the money", is
   [F6 in the multi-agent cost plan](../router/multi-agent-cost-plan.md). It needs per-session savings data (F1)
   and its own ADR.

## Open questions

- Does `x-claude-code-agent-type` arrive as documented through this router? It needs
  `CLAUDE_CODE_GATEWAY_HINT_HEADERS=1` and has not been captured.
- Does the auto-mode classifier's request really carry `request-class: auxiliary`? The docs list classifiers under
  `auxiliary` but do not say which classifiers.
- What does a fair relative floor look like on this router's score scale? It needs graded data per category.
- Could the router check tool-calling ability before sending a tool-heavy `Explore` subagent to a small local
  model? **Partly answered (2026-10-02): not with today's data.**
  - `ToolCallCapabilityStore` records a model's tool-call dialect and never records failures. Its own docs note
    that "chose not to call a tool" and "cannot call tools" look the same at that layer.
  - Capability-aware routing is the subject of ADR-0017, which is blocked on tracked TODO #8.
