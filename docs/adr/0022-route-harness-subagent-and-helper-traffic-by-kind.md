# 0022. Route harness subagent and helper traffic by kind

**Status:** proposed
**Date:** 2026-10-02
**Deciders:** David Pizon

## Context and Problem Statement

Issue [#163](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/163) makes the router recognize
vendor-documented markers that a request comes from a subagent or a helper task (session titles, commit messages,
summaries). The detection is settled; the signal table is in
[`docs/router/utility-model-routing.md`](../router/utility-model-routing.md#subagent-and-side-task-signals). What
is not settled is what the router should **do** with a detected request.

David defined the goal on 2026-10-02 as **the best quality for the money**. The first plan sent every signalled
request to `UtilityRoutingPolicy`, the router's price-weighted rule. The Phase 1 research
([`docs/research/subagent-and-helper-routing-evidence.md`](../research/subagent-and-helper-routing-evidence.md))
showed that this conflicts with how the harness vendors treat the same traffic:

- They keep subagents on the main model.
- They put only helpers on cheap models.
- They keep a safety classifier that shares the helper request class on a strong model.

It also showed that this router cannot grade most of this traffic.

The choice is hard to reverse quietly: it decides which model does real agent work, and it changes what the router
learns. That makes it a decision rather than an implementation detail.

## Decision Drivers

- **Best quality for the money** (David, 2026-10-02): neither cheapest nor best regardless of price.
- **Follow vendor evidence**, not hypotheses:
  - Claude Code, Codex and VS Code Copilot run subagents on the main model and move them to a cheaper model only
    for narrow, read-only work, and only by opt-in ([Claude Code subagents][cc-sub],
    [Codex subagents][codex-sub], [VS Code subagents][vsc-sub]).
  - Helpers are cheap by design ([VS Code language models][vsc-lm], [Claude Code costs][cc-cost]).
  - Anthropic's own multi-agent system used Sonnet, not the cheapest model, for subagents
    ([Anthropic research system][anth-multi]).
- **Don't put safety decisions on the cheap path.**
  - Claude Code's `auxiliary` request class includes classifiers ([gateway guide][cc-gw]).
  - The auto-mode classifier runs on Sonnet 5 by default rather than the session's model
    ([permission modes][cc-perm]).
- **Respect what the router can measure.**
  - Only responses with a fenced code block are graded (`CodeBlockSignalExtractor`).
  - Helpers are never graded, and subagent turns rarely are.
  - A quality floor protects only where grades exist.
- **No behavior change without a signal**, and no reroute of an explicit model pick (ADR-0005).
- **Don't add learning bias.** A path that never explores and records propensity 1.0 must not carry traffic whose
  grades feed router memory.

## Considered Options

- Option 1: Every signal to the utility rule (the first plan).
- Option 2: Every signal to the learned path.
- Option 3: Subagents to the learned path, helpers to the utility rule, with an absolute subagent quality floor
  and no helper floor.
- Option 4: Route by kind, following vendor defaults.
  - Helpers go to the utility rule, only when the client delegated the choice.
  - Read-only search and documentation subagents go to the utility rule, restricted to near-best models.
  - Every other subagent goes to the learned path.
- Option 5: A cost-aware vote on the learned path for all signalled traffic.

## Decision Outcome

Chosen option: "Option 4: route by kind, following vendor defaults".

It is the only option that meets **best quality for the money** and **follow vendor evidence** together:

- It saves money exactly where every vendor already accepts a cheaper model: helpers, `Explore`, and
  `claude-code-guide`, which Anthropic already runs on Haiku.
- It keeps quality-first routing where every vendor keeps the main model.
- The **delegation rule** keeps the safety classifier off the cheap path. Bias applies only when the requested
  model is `auto`, `totallyhot-arcrouter` or a Copilot `copilot-utility*` alias, and the classifier names a
  specific model.
- It respects **what the router can measure**. The floor for light subagents is relative, which is how routing
  research states quality targets ([RouteLLM][routellm]). It requires a known score, so an unscored model can't
  pass it.
- It adds no learning bias. Graded subagent traffic stays on the path that explores.

Option 5 is the fuller answer, but it changes the default router's behavior for everyone. It needs per-session
savings data first. It is tracked as F6 in [`docs/router/multi-agent-cost-plan.md`](../router/multi-agent-cost-plan.md).

The plan [`docs/plans/issue-163-subagent-aware-routing.md`](../plans/issue-163-subagent-aware-routing.md) holds
the class table, the options and the tests.

### Consequences

- Good, because helper traffic, the case all three vendors agree is cheap, gets the price-weighted rule.
- Good, because `Explore` and `claude-code-guide` go cheaper only to a model whose known score is within the
  relative floor (0.9 by default) of the best candidate. With no scores, they route normally.
- Good, because every other subagent routes exactly as it does without a signal. The signal still shows in the
  log line and, under ADR-0021, on the dashboard.
- Good, because helpers are ungraded and graded subagents stay on the exploring path, so no new learning bias is
  introduced.
- Bad, because the savings are much smaller than the first plan's:
  - Helpers are a small share of spend (Claude Code puts background work under $0.04 per session [cc-cost]).
  - Most subagent spend is unchanged until F1 data justifies widening the light class, or F6 lands.
- Bad, because the light-subagent split depends on `x-claude-code-agent-type`, a hint header Claude Code sends to
  a gateway only when `CLAUDE_CODE_GATEWAY_HINT_HEADERS=1`. Without it, no helper or light subagent is detected,
  which is safe but saves nothing.
- Bad, because one classifier case remains:
  - If the auto-mode classifier falls back to the session model and that model is `auto`, its request is routed
    as a helper, and the headers can't tell it from a title.
  - Mitigations are a documented operator step (list the classifier's model explicitly) and a toggle.
- Bad, because the relative floor reads code-quality scores, not tool-calling ability, while `Explore` is
  tool-heavy. Whether to exclude models that fail the existing tool-call capability probing is open in the plan.
- Neutral, because `RoutingContext` gains one optional trailing member (the route class), and
  `CompositeRoutingPolicy` gains one dispatch branch.
- Neutral, because the 0.9 floor is a starting point, not a measurement. Codex gets visibility but no routing
  change until it sends an agent type.

## Pros and Cons of the Options

### Option 1: Every signal to the utility rule

- Good, because it is the largest saving and reuses existing code unchanged.
- Bad, because it moves real subagent work, which every vendor keeps on the main model, onto a rule whose
  exchange rate (0.1 quality per $1 per million tokens) almost never picks a premium model.
- Bad, because `auxiliary` includes classifiers, so the auto-mode safety check could land on the cheapest model.
- Bad, because graded subagent turns would feed router memory from a path that never explores, which is the
  learning bias raised in review.

### Option 2: Every signal to the learned path

- Good, because it is quality-first with real exploration and no learning bias.
- Bad, because the learned path ignores price, so helper traffic would pay main-model prices for titles. That is
  the opposite of every vendor's helper default, and it fails "for the money".
- Bad, because the signal would then change nothing about routing, and #163 would deliver visibility only.

### Option 3: Split, with an absolute subagent floor and no helper floor

- Good, because it keeps helpers cheap and protects subagents.
- Bad, because the floor that matters most never fires. Helpers are never graded, so "no helper floor" and "0.3"
  behave the same, and the real risk (the classifier) is not addressed.
- Bad, because an absolute subagent floor (for example 0.6) has no evidence behind its value and ignores how good
  the best available model is in that category.
- Bad, because it sends every subagent to the price-weighted rule, including the open-ended ones vendors keep on
  the main model.

### Option 4: Route by kind, following vendor defaults

- Good, because each class matches documented vendor behavior.
- Good, because the delegation rule and the relative floor answer the two concrete risks: the classifier, and
  unscored cheap models.
- Bad, because the savings are smaller and depend on an opt-in header.
- Bad, because it adds a class mapping and a policy branch to maintain.

### Option 5: A cost-aware vote on the learned path

- Good, because it is the complete "best quality for the money": the learned path's quality estimate with price
  in the vote.
- Bad, because it changes routing for all traffic, not only signalled traffic, which breaks #163's "no change
  without a signal".
- Bad, because its weights need the per-session savings data F1 will produce, and
  [how-it-learns.md](../how-it-learns.md#so-where-does-cost-actually-come-in) already calls it a change in
  behavior that needs its own ADR.

## More Information

- Evidence: [`docs/research/subagent-and-helper-routing-evidence.md`](../research/subagent-and-helper-routing-evidence.md)
  (all sources read 2026-10-02).
- Plan: [`docs/plans/issue-163-subagent-aware-routing.md`](../plans/issue-163-subagent-aware-routing.md), Phase 2b.
- Related:
  - [ADR-0005](0005-protect-explicit-provider-selections-from-silent-substitution-on-any-circuit-trip.md):
    explicit picks are never rerouted.
  - [ADR-0021](0021-carry-the-subagent-routing-signal-on-the-telemetry-wire-as-an-optional-field.md):
    the signal on the telemetry wire.
  - F1 and F6 in [`docs/router/multi-agent-cost-plan.md`](../router/multi-agent-cost-plan.md).
- Revisit when:
  - F1 receipts show per-category grades for light-subagent traffic, to tune the floor or widen the class.
  - Codex sends an agent type.
  - F6 is accepted.

Sources:

- [cc-sub]: Claude Code, "Subagents" — https://code.claude.com/docs/en/sub-agents
- [cc-cost]: Claude Code, "Manage costs effectively" — https://code.claude.com/docs/en/costs
- [cc-gw]: Claude Code, "Gateway compatibility guide" — https://code.claude.com/docs/en/llm-gateway-protocol
- [cc-perm]: Claude Code, "Choose a permission mode" (auto mode, cost and latency) — https://code.claude.com/docs/en/permission-modes
- [codex-sub]: OpenAI Codex, "Subagents" — https://learn.chatgpt.com/docs/agent-configuration/subagents
- [vsc-sub]: VS Code, "Use subagents" — https://code.visualstudio.com/docs/copilot/agents/subagents
- [vsc-lm]: VS Code, "AI language models in VS Code" — https://code.visualstudio.com/docs/agent-customization/language-models
- [anth-multi]: Anthropic, "How we built our multi-agent research system" — https://www.anthropic.com/engineering/multi-agent-research-system
- [routellm]: Ong et al., "RouteLLM: Learning to Route LLMs with Preference Data" (2024) — https://arxiv.org/abs/2406.18665

[cc-sub]: https://code.claude.com/docs/en/sub-agents
[cc-cost]: https://code.claude.com/docs/en/costs
[cc-gw]: https://code.claude.com/docs/en/llm-gateway-protocol
[cc-perm]: https://code.claude.com/docs/en/permission-modes
[codex-sub]: https://learn.chatgpt.com/docs/agent-configuration/subagents
[vsc-sub]: https://code.visualstudio.com/docs/copilot/agents/subagents
[vsc-lm]: https://code.visualstudio.com/docs/agent-customization/language-models
[anth-multi]: https://www.anthropic.com/engineering/multi-agent-research-system
[routellm]: https://arxiv.org/abs/2406.18665
