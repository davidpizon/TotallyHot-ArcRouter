# Plan: Subagent-aware routing (#163)

**Status:** Revised 2026-10-02 after the Phase 1 research. **Approved by David on 2026-10-02 (in a Claude Code session), with the open points accepted at their stated defaults** (see Decisions). The first version of this plan was approved by merging PR #175. Under it, Phase 1, Phase 2 and the Phase 3 log line were built on branch `feature/163-subagent-aware-routing` (commits `09fd18c`, `c41c6f0`, `533b47e`). Phase 2b below reworks that code to the revised design.
**Issue:** [#163](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/163) — "P1: Subagent-aware routing (cheaper-model bias for harness subagent / side-task requests)".
**Standing rule:** [Approved plan before coding](../router/standing-rules.md#approved-plan-before-coding). The implementation pull request must link this plan once approved.
**Evidence:** [`docs/research/subagent-and-helper-routing-evidence.md`](../research/subagent-and-helper-routing-evidence.md) (what vendors do, and what the router can measure). The signal table is in [`docs/router/utility-model-routing.md`](../router/utility-model-routing.md#subagent-and-side-task-signals).
**Decision records:** [ADR-0022](../adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md) (route by kind, proposed); [ADR-0021](../adr/0021-carry-the-subagent-routing-signal-on-the-telemetry-wire-as-an-optional-field.md) (telemetry wire field, proposed).
**ADR-0008 Amendment 1:** Binding. This plan adds a feature. It schedules no smell audit, splits no files, and refactors nothing beyond what the feature needs.
**Out of scope:**
- The semantic cache (#164) and every other P1/P2 item.
- A cost-aware vote on the learned path. That is [F6 in the multi-agent cost plan](../router/multi-agent-cost-plan.md) and needs its own plan and ADR.

## Goal

When a harness marks a request as a subagent or a helper task, route it for **the best quality for the money** (David, 2026-10-02), as far as the evidence supports:

- Helper tasks lean cheap.
- Read-only search subagents may go cheaper, but only to a model that is known to be nearly as good as the best one.
- Every other subagent keeps the router's normal quality-first routing.

With no signal, or a malformed, ambiguous or disabled one, routing behaves exactly as it does today.

## Why the design changed

The first design sent every signal to `UtilityRoutingPolicy`, which ranks mostly on price. The Phase 1 research found four problems with that:

1. **Vendors don't route subagents cheap by default.**
   - Claude Code, Codex and VS Code Copilot all run subagents on the main model.
   - They move a subagent to a cheaper model only for narrow, read-only or repetitive work, and only when someone opts in.
   - Helpers, by contrast, are cheap by vendor design.
2. **The router can't grade most of this traffic.** Only responses with a fenced code block are graded (`CodeBlockSignalExtractor`).
   - Helper output is never graded, so a helper quality floor never fires.
   - Subagent turns, which are mostly tool calls, are rarely graded.
3. **One helper class hides a safety decision.**
   - Claude Code's `auxiliary` request class includes classifiers.
   - The auto-mode classifier, which decides whether an action may run, deliberately runs on Sonnet 5 rather than a cheap model.
4. **The utility rule's exchange rate.** 0.1 of quality is worth $1 per million tokens, so premium models almost never win, even for hard subagent work.

The research doc has the sources for each point.

## What exists today (from CodeGraph and the branch)

- `SubagentSignalDetector` (branch): a pure function over headers and body.
  - It returns a `SubagentSignal(Harness, Kind, Source)` for Claude Code's agent-id and request-class headers, Codex's `x-codex-turn-metadata`, and Copilot `copilot-utility*` aliases.
  - Anything ambiguous, malformed, oversized or conflicting returns `null`.
- `RequestClassification.Subagent` (branch): an optional trailing member that records the signal.
- `RequestInterceptor.ResolveModelRouteAsync` (branch):
  - Applies the signal only on the agentic-routing path, as `IsUtility = true`.
  - Explicit configured picks keep their model and classification (ADR-0005).
  - The switches are read live from `Routing:SubagentBias`.
- `CompositeRoutingPolicy` sends `IsUtility` requests to `UtilityRoutingPolicy` and everything else to `OrchestratorRoutingPolicy`.
  - `UtilityRoutingPolicy` ranks `ε₁·quality + ε₂·price` with an observed-score floor, never explores, and records propensity 1.0.
  - `OrchestratorRoutingPolicy` is quality-only, explores 5%, and records real propensity.
- `IsRouterChoiceModelName` recognizes `auto` and `totallyhot-arcrouter` as "the router should choose".
- The routing log line (branch) ends with `subagentSignal=<harness/kind|none>`.

## Design

### Route classes

The detector already reports each signal's harness and kind. A small pure mapping, `SubagentRouteClass`, turns that into one of four route classes:

| Class | Signals | Route |
|---|---|---|
| **Helper** | Claude Code `x-claude-code-request-class: auxiliary`; Copilot `copilot-utility*` model aliases | The existing utility rule, unchanged: `ε₁·s + ε₂·κ` with the `UtilityMinQualityScore` floor (0.3). In practice the floor never fires because helpers are ungraded, but it is harmless. |
| **Light subagent** | Claude Code subagent whose `x-claude-code-agent-type` is `Explore` or `claude-code-guide` | The utility rule restricted to candidates whose **known** score in this category is at least `LightSubagentRelativeFloor` × the best known score among the candidates. The best-scoring candidate always qualifies, so the rule picks the best value among the near-best. If no candidate has a known score, or none of the qualifiers is priced, the request routes normally. |
| **Subagent** | Claude Code subagent of any other type (`Plan`, `general-purpose`, `statusline-setup`, `custom`, `teammate`, `fork`) or with no type (agent-id or `request-class: subagent` only); Codex `thread_spawn` and `memory_consolidation` | Normal routing: the learned path, exactly as without a signal. The signal is recorded for the log line and dashboard only. |
| **None** | Claude Code `main`, `compaction`, `workflow`; Codex `compact`, `guardian`, `review`, `agent_job:*`, `other`; anything malformed, ambiguous or conflicting | Today's routing, with no signal recorded. |

Why each line sits where it does:

- **`Explore` and `claude-code-guide`.** Anthropic suggests Haiku for `Explore`, and already runs `claude-code-guide` on Haiku.
- **`Plan`, `general-purpose` and the rest.** Every vendor runs these on the main model.
- **Codex subagents.** Codex sends no agent type, so narrow work can't be told apart from open-ended work.
- **Codex memory consolidation.** It reportedly defaults to the full model (unverified), and its output persists into later sessions.

### Delegation rule

Helper and light-subagent bias applies only when the client handed the choice to the router. That means the requested `model` is a router-choice name (`auto`, `totallyhot-arcrouter`) or a Copilot `copilot-utility*` alias.

- A request naming a specific model the router doesn't know still takes today's unresolved-name fallback, but without bias.
- A request naming a configured model never reaches a policy (ADR-0005), as today.

Why: the auto-mode classifier names its model (Sonnet 5 by default), while titles and summaries carry the main model, which is `auto` under this repo's Claude Code preset. This rule keeps the classifier off the cheap path even though it shares the `auxiliary` class.

**Residual risk.** If the classifier falls back to the session's model, and that model is `auto`, a classifier request would be routed as a helper. The headers can't distinguish it from a title. The mitigations:

- Phase 5 docs tell operators to list the classifier's model in the router so it is always an explicit pick.
- The `ClaudeCodeHintHeaders` toggle turns off the request-class signal entirely.

### Relative floor

`LightSubagentRelativeFloor` defaults to **0.9**, meaning "within 10% of the best known candidate". Routing research states quality targets this way; RouteLLM, for example, measures against a fraction of the strong model's quality. **0.9 is a starting point, not a measured value.** It should be retuned once F1 (per-session savings receipts) shows graded data per category.

The scores are the grader's code-quality scores from graded traffic in the same category. They are a proxy for "can this model code", not for "can it drive tools".

### Learning

| Class | Effect on what the router learns |
|---|---|
| Helper | None. Helpers are ungraded and write nothing to router memory, so there is no learning bias and no point exploring. |
| Light subagent | Small. The pick is deterministic (propensity 1.0), but these turns are rarely graded. Recorded as a residual risk. |
| Subagent | None added. The learned path explores and records real propensity. |

Recording the signal on each transcript row, so cost-driven picks can be filtered from learning, is a follow-up in ADR-0021 with a stated trigger. It is not built here.

### Options (`Routing:SubagentBias`, read live)

`Enabled` (kill switch), `ClaudeCodeAgentId`, `ClaudeCodeHintHeaders`, `CodexTurnMetadata`, `CopilotUtilityAlias`, `LightSubagentRelativeFloor`.

`ClaudeCodeHintHeaders` replaces the branch's `ClaudeCodeRequestClass` and covers both hint headers, `request-class` and `agent-type`. `LightSubagentRelativeFloor` is 0.9 by default, in the range (0, 1].

## Phase 1 — Signal research. **Done (2026-10-02).**

- The signal table is in `utility-model-routing.md`. The routing evidence is in the research doc.
- Supported: Claude Code (headers), Codex (turn metadata), Copilot (aliases; wire evidence unverified).
- Documented as unsupported: Cursor and Aider. Neither sends a marker the router can see.
- **Gate:** David reviewed the table on 2026-10-02 and asked for the routing evidence that produced this revision.

## Phase 2 — Detector and classification plumbing. **Done under the first design** (`c41c6f0`).

The detector, `RequestClassification.Subagent`, `SubagentBiasOptions`, the interceptor wiring and their tests stay. Phase 2b changes how a signal is routed, not how it is detected.

## Phase 2b — Route by class. **Done (2026-10-02).**

**Where the build differs from the text below (deliberate, recorded per the standing rule):**
- **`RoutingContext` member.** It carries `NearBestValueFloor` (the live floor value) instead of a route class. The
  composite needs the number, not the label. Helpers still travel as `IsUtility`, and `Normal` needs nothing.
- **Enum name and values.** The route class enum is `SubagentRouteClass` with `Normal`, `LightSubagent` and
  `Helper`. The class this plan calls "Subagent" is `Normal` in code.
- **Quality gate still applies.** The near-best selection also applies the utility rule's `UtilityMinQualityScore`
  gate. If the best known score is below 0.3, the request routes normally.
- **Light subagents ignore the payload heuristic.** A light subagent's documented marker outranks the payload
  heuristic. Even when `max_tokens` ≤ 64 would make it "utility", it gets the near-best rule.
- **Hint headers off means ignored.** With `ClaudeCodeHintHeaders` off, the hint headers are ignored entirely. A
  `main` class next to an agent id is then just an agent-id signal, not a contradiction.
- **Agent type needs an agent id.** An agent type without an agent id is treated as contradictory (no signal),
  because Claude Code only sends the type on a spawned agent's own turns.
- **Route class on the log line.** It landed in this phase rather than in Phase 3.
- **Native Messages restriction (David, 2026-10-02: option A).** Added after comparing with ADR-0017.
  - **Rule:** a helper or light-subagent request on `/v1/messages` only considers `anthropic` candidates, because
    native Messages traffic is untranslated. With none eligible, the bias is withdrawn and the request routes
    normally (logged `route=normal`).
  - **Census:** the TODO #8 census now also records the Claude Code and Codex subagent markers (fixed vocabulary
    only), to answer ADR-0022's open questions.

**Tool-calling investigation (the open point):** no filter is built, because the router has no data to filter on.
- **What `ToolCallCapabilityStore` holds.** It records a model's tool-call *dialect*, learned from its chat
  template or from matched calls. By design it never records failures: its own docs say "chose not to call a
  tool" and "cannot call tools" produce identical evidence at that layer.
- **Where capability-aware routing belongs.** ADR-0017 (proposed) covers pinning auto-routed requests to capable
  models. It is blocked on tracked TODO #8's traffic capture.

The risk stays recorded below.

**Verify with CodeGraph before editing:** `RoutingContext` (67 references), `CompositeRoutingPolicy`, `UtilityRoutingPolicy`, and the `RequestInterceptor` hub. Do not touch `ManagementFacade`.

- **Detector:**
  - Read `x-claude-code-agent-type` under the same rules as the other headers: single value, printable ASCII, bounded length.
  - Map it to a fixed vocabulary. An unknown value means "subagent" (normal routing); the detector never copies the value itself.
  - Add `SubagentRouteClass` as a pure mapping from the signal to Helper, Light subagent, Subagent or None.
- **Interceptor:**
  - Apply the delegation rule.
  - Helper: set `IsUtility = true`, as today.
  - Light subagent: pass the class to the policy.
  - Subagent: record the signal on the classification without changing `IsUtility`.
- **`RoutingContext`:** one optional trailing member carrying the route class. Positional callers compile unchanged.
- **`CompositeRoutingPolicy`:** send a light subagent to a new `UtilityRoutingPolicy` relative-floor selection. When that returns nothing, use the normal non-utility dispatch. `DecideOutcomeAsync` mirrors `SelectModelAsync`, as it does today.
- **`UtilityRoutingPolicy`:** add the relative-floor selection. It returns `null` when no candidate has a known score or no qualifier is priced, and never applies its own degradation fallbacks to this class.
- **Options and `appsettings.json`:** as in the options section above.
- **Tests in this phase** (each well under 5 s):
  - Each class routes as specified.
  - Delegation rule: `auto` and Copilot aliases get the bias; an unresolved specific name gets today's routing; a configured name is an explicit pick.
  - Relative floor:
    - A cheaper candidate within the floor wins on value.
    - A cheaper candidate below the floor is excluded, and the best-value near-best model wins.
    - No candidate has a known score, which routes normally.
    - The only qualifiers are unpriced, which routes normally.
    - Free providers count as priced at 0.
  - An unknown agent type routes normally.
  - Each toggle works, and the kill switch works.
  - No signal leaves routing unchanged.
  - The existing utility-routing tests pass untouched.

**Exit criterion:** the build has no warnings, all tests are green, and coverage is at least 80%.

## Phase 3 — Log line, telemetry event and dashboard

- **Log line:** done.
  - `533b47e` added the signal, and Phase 2b added the route class.
  - The line now ends, for example, `subagentSignal=claude-code/explore, route=light-subagent`.
  - `route` is one of `none`, `normal`, `light-subagent` or `helper`.
  - The template is a static string, and both values come from fixed vocabularies.
- **Telemetry field and Live Stream badge:** [ADR-0021](../adr/0021-carry-the-subagent-routing-signal-on-the-telemetry-wire-as-an-optional-field.md) is proposed. Nothing on the proto, `RoutingTelemetryEvent`, the publisher or the GUI changes until David accepts it. Once accepted:
  - Add the optional `subagent_signal = 25` field and the GUI chain.
  - Add a bUnit test for the badge, following `docs/gui/DESIGN.md` (and `docs/gui/MOTION.md` if it animates).
  - A missing field renders nothing.

**Exit criterion:** a signalled request's log line, telemetry event and Live Stream row all name the signal and the chosen model. An unsignalled one shows none.

## Phase 4 — Full test matrix

Follow `RequestInterceptorRoutingPolicyTests`, `CompositeRoutingPolicyTests` and `UtilityRoutingPolicyTests`, with real policies where the cost outcome matters:

- **Helper:** with a priced cheap candidate and an expensive one, the cheap one is chosen.
- **Light subagent:**
  - With the best candidate at 0.9 and a $0 candidate at 0.85, the floor is 0.81 and the free one is chosen.
  - With the free candidate at 0.7 instead, it is excluded and the near-best candidate is chosen.
  - With no known scores, the request routes normally.
- **Subagent:** the selection is identical to the same request with no signal.
- **Allowlist:** a signalled request never resolves outside `ListModels()`, and never to a model whose circuit is open.
- **Explicit model pick with a signal:** unchanged (ADR-0005).
- **Copilot aliases:**
  - These get the bias: `copilot-utility`, `copilot-utility-small`, `Copilot-Utility-Small`, and the unseen tier `copilot-utility-tiny`.
  - These don't: `copilot-utilit`, `my-copilot-utility`.
- **Regression:** `HeuristicRequestClassifierTests` and the existing routing-policy tests are untouched and green.

## Phase 5 — Docs and proof

- **`docs/router/utility-model-routing.md`:**
  - Add the route classes, the delegation rule, the relative floor and the config keys.
  - Correct the false "Shipped (Phase H): Router and utility alias recognition" status and the never-implemented `RouterAlias` / `UtilityAliases` options.
- **`docs/research/technical-reference.md` E.3:** link to that section.
- **`docs/install/harnesses/claude-code.md`:**
  - `CLAUDE_CODE_GATEWAY_HINT_HEADERS=1` is required for the helper and light-subagent bias.
  - List the auto-mode classifier's model in the router so it stays an explicit pick.
  - Don't point the model variables the classifier falls back to at `auto`.
  - The preset itself changes only as far as these notes need. Preset rework stays out of scope.
- **PR body proof, as hosted artifacts:**
  - The `dotnet test` output.
  - A request set showing a helper routed cheap, an `Explore` subagent routed cheap or normal according to the floor, a `general-purpose` subagent routed normally, and an unsignalled request unchanged, each with its log line.
  - Say whether the requests were live or replayed.

**Exit criterion:** the full build and test suite are green with no warnings, and the issue's "Done when" checklist is ticked with links.

## Risks

- **Smaller savings than the first design.**
  - Only helpers and the two light subagent types get cheaper.
  - Helpers are a small share of spend: Claude Code puts background work under $0.04 per session.
  - Most subagent spend routes as it does today, until F1 data justifies widening the light class, or F6 adds cost to the learned path.
- **Classifier residual.** The fallback case is in the delegation rule above. It is documented, and it has a toggle.
- **Tool-heavy light subagents on weak local models.**
  - `Explore` calls Read, Grep and Glob. The relative floor checks code-quality scores, not tool-calling ability.
  - Small LM Studio models have been seen echoing tool-call instructions as text (see the `lmstudio` comment in `appsettings.json`).
  - **Investigated in Phase 2b, not mitigated.** The tool-call capability store records dialects, never failures, so it can't exclude a model that handles tools badly. Capability-aware routing belongs to ADR-0017 (blocked on tracked TODO #8). Until then, the mitigations are the relative floor (a free model must have a known score close to the best) and the `ClaudeCodeHintHeaders` toggle, which turns the light-subagent route off.
- **Free local models.** Helper traffic will usually land on `IsFree` providers (κ = 0). That is fine for titles. Light subagents reach them only by clearing the relative floor, which a model with no score can't do.
- **Hint headers are opt-in.**
  - Without `CLAUDE_CODE_GATEWAY_HINT_HEADERS=1`, Claude Code sends only the agent-id header. Every subagent then routes normally and no helper is detected.
  - This is safe but saves nothing.
- **Signals may not exist, or may be dropped.** A harness with no marker is documented as unsupported. An intermediate router that strips headers silently disables the bias, with no other effect.
- **Forged headers.** These are a cost preference inside the operator's allowlist and quality gate, not an authorization boundary.
- **Wider blast radius than the first design.** `RoutingContext` and `CompositeRoutingPolicy` change. The changes are kept to one optional member and one dispatch branch.
- **Header volume.** The detector reads a few named headers and never enumerates them all.

## Decisions

David, 2026-09-29:
1. `Routing:SubagentBias:Enabled` defaults to on. Only verified signals ship, and the kill switch stays.
2. A harness with no verifiable marker is documented as unsupported.
3. Telemetry: the log line and the dashboard. The additive proto field needs an ADR first (ADR-0021).

David, 2026-10-02:

4. The goal is the best quality for the money.
5. Route by class, following the vendor evidence ([ADR-0022](../adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md)). Keep the research in `docs/research/subagent-and-helper-routing-evidence.md`.

6. The revision is approved, and its open points are accepted at their defaults:
   - the 0.9 relative floor;
   - `claude-code-guide` in the light class;
   - Codex `memory_consolidation` routing normally;
   - the `ClaudeCodeRequestClass` → `ClaudeCodeHintHeaders` rename.

   Whether light subagents exclude models without verified tool calling stays an investigation in Phase 2b, reported back before anything is built.
