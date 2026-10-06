# Plan: Subagent-aware routing (#163)

**Status:** Revised 2026-10-02 after the Phase 1 research. **Approved by David on 2026-10-02 (in a Claude Code session), with the open points accepted at their stated defaults** (see Decisions). The first version of this plan was approved by merging PR #175. Under it, Phase 1, Phase 2 and the Phase 3 log line were built on branch `feature/163-subagent-aware-routing` (commits `09fd18c`, `c41c6f0`, `533b47e`). Phase 2b below reworks that code to the revised design. **Phase 2c was added on 2026-10-02 at David's request, and he approved it the same day (see Decisions 9–11).**
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

## Phase 2c — Strip what the picked model rejects. **Approved by David on 2026-10-02, with the recommended option on all three decisions below. Built and smoke-tested 2026-10-02.**

**Where the build differs from the text below (deliberate, recorded per the standing rule):**
- **One extra request per scan.** The capability read is a separate step, `ProviderEndpointScanner.ScanModelFeaturesAsync`.
  It runs from the shared scan core (`ScanAndPersistCapabilitiesAsync`) only when the endpoint answered
  Anthropic-shaped. That costs one more `GET /v1/models?limit=1000` per scan of such a provider, never one per request.
  It keeps endpoint flavors and per-model facts apart, as the existing per-model metadata step already does, and a
  failed or partial read changes nothing.
- **Names.** The record is `ModelFeatureSupport`, read through `IModelFeatureSupportStore`, stored in
  `model_feature_support`. The strip is `MessagesFeatureStripper` producing a `MessagesFeatureStrip`.
- **No copy unless something goes.** The strip is planned read-only against the shared parsed body. A candidate's
  own copy is parsed only when the plan removes something.
- **Two edge rules the table did not spell out.**
  - `thinking` also goes when the record's own `thinking.supported` is false, whatever the type.
  - A `context_management` object holding members besides `edits` is kept with its remaining edits, and its beta
    value stays. Only an object whose edits were all it held goes with its beta value.
- **Feature names.** The log line and header use `thinking.<type>`, `output_config.effort`,
  `context_management.<strategy>` and `context_management`, restricted to letters, digits, `_`, `.` and `-`.
- **GUI shape.** The admin contract carries the record regrouped generically (`ModelCapabilitiesState`: groups,
  each with options), not raw JSON and not one field per feature, so a new capability needs no contract change.
  The badges show only the three strippable families. "Not supported" uses muted slate rather than red, because it
  is a fact about the model, not a failure.
- **Review follow-ups (PR #186).**
  - A scan whose model list answers in OpenAI shape now clears the provider's records, so a key re-pointed away
    from Anthropic stops stripping by stale data. A scan whose list failed still keeps them.
  - An `X-ArcRouter-Stripped-Features` header copied from the upstream response is removed when the answering
    candidate stripped nothing.
- **Docs.** The `README.md` header table gains `X-ArcRouter-Stripped-Features`, and
  `docs/install/harnesses/claude-code.md` gains "Models that reject what Claude Code sends", which is work step 6.
- **Smoke run, 2026-10-02 (the pull-request proof and the golden-path smoke).** The router built from this branch ran
  against a localhost stand-in provider (`anthropic-smoke`). It served `/v1/models` with Haiku 4.5's real record and
  a fully capable `claude-sonnet-5`, and recorded every forwarded request.
  - **Isolation.** Every storage path, the provider list and the logs (`LOGS_DIRECTORY`) were redirected to a scratch
    folder, and MCP, update checks, transcripts, automatic retrains and the LLM voter were switched off. On Windows the
    machine-shared directory cannot be redirected, so `routing-gate.json` was switched on for the run with David's
    approval. It and `web-interface.json` were restored byte for byte, and a before/after snapshot of
    `C:\ProgramData\TotallyHotArcRouter` matched on all 52 files.
  - **Scan and GUI.** "Refresh" on the provider stored both records, and the Providers tab showed amber
    Thinking, grey Effort and amber Context mgmt for Haiku, and green for all three on Sonnet.
  - **Claude Code 2.1.286 through the router (`model: auto`).** Nine requests reached the stand-in: main turns, the
    `Explore` subagent, three WebFetch helpers and compaction. All went to Haiku and none carried `thinking`,
    `output_config` or `context_management`. The `effort-` beta value was gone everywhere, and the
    `context-management-` value was gone wherever its field had been sent. The router logged one strip line per request.
  - **Failover.** With Haiku answering 503, the stripped Haiku attempt failed over to Sonnet, which received the
    full request and every beta value. The response came back with `X-ArcRouter-Substitution-Reason: Failover` and
    no strip header.
  - **Explicit pick.** `model: claude-haiku-4-5` was forwarded with thinking, effort and both beta values intact.
  - **Transport.** Claude Code reached the router only on the opt-in plain-HTTP listener
    (`Proxy:PlainHttp`). Over the HTTPS port, with `NODE_EXTRA_CA_CERTS` set to the router CA, every attempt ended in
    "Connection error", while Node itself connected with the same bundle. That is unrelated to this phase and is
    tracked separately; the strip runs identically on both listeners.

**Why.** Under option A, helper and light-subagent traffic on `/v1/messages` only considers `anthropic` candidates. The default `appsettings.json` lists Claude Haiku 4.5 among them, and Claude Code sends fields that Haiku 4.5 rejects.

- **What Claude Code sends under `auto`.** Captured on 2026-10-02 with Claude Code 2.1.286 pointed at a localhost stand-in for the Anthropic API, using this repo's preset plus `CLAUDE_CODE_GATEWAY_HINT_HEADERS=1`. Nothing reached Anthropic.

  | Request (route class) | `thinking` | `output_config.effort` | `context_management` edits |
  |---|---|---|---|
  | `auxiliary`, the WebFetch summary (helper) | not sent | `high` | not sent |
  | `subagent`, type `Explore` (light subagent) | `adaptive` | `high` | `clear_thinking_20251015` |
  | `main` and `compaction` (no bias) | `adaptive` | `high` | `clear_thinking_20251015` |

  Every `auto` request also sends the `effort-2025-11-24` and `context-management-2025-06-27` beta values. The auto-mode classifier names `claude-sonnet-5` and sends `thinking: {"type": "disabled"}`, so it is an explicit pick and unaffected.
- **What Haiku 4.5 accepts.** Adaptive thinking returns a 400 on Haiku 4.5, Sonnet 4.5 and Opus 4.5 ([Extended thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking)). Haiku 4.5 and Sonnet 4.5 are missing from the supported-models list for effort ([Effort](https://platform.claude.com/docs/en/build-with-claude/effort)).
- **Why Haiku gets picked.** Helpers are ungraded, so `UtilityRoutingPolicy` ranks them on price alone, and Haiku 4.5 is the cheapest `anthropic` model. `Explore` can reach it through the near-best rule, or through the learned path over the `anthropic`-only pool.
- **What happens today.** A plain 400 doesn't fail over, and its body reaches Claude Code byte-for-byte (`UpstreamFailureClassifier`, `UpstreamResponseWriter`), so Claude Code's own recovery runs:
  - After a thinking rejection, it retries and keeps thinking off for that conversation.
  - After an effort rejection, it leaves effort out of later requests "to that model until Claude Code exits" ([gateway guide](https://code.claude.com/docs/en/llm-gateway-protocol#automatic-retry-and-error-forwarding)). That model is `auto`, so the main conversation most likely loses effort too. This is inferred from the guide, not observed.
- **Not only biased traffic.** The learned path can also pick Haiku 4.5 for unbiased `auto` traffic: main turns, compaction and other subagents. That exposure predates option A.

**Design rule (David, 2026-10-02):** prefer "routing rules that were not pinned to specific agent versions when possible". This phase reads that as two constraints:

- No model id, family or generation appears in the rule. Support comes from the picked model's own capability record.
- Nothing depends on which harness, or which harness version, sent the request. The strip keys on the body fields present.

Versioned strings appear only as data:
- A context-management strategy in the request is matched exactly against the vendor's own capability key.
- A beta value is matched by its feature prefix (`effort-`, `context-management-`), so a newly dated value needs no code change.

**Capability source.** Anthropic's Models API returns a `capabilities` object for each model ([Get a model](https://platform.claude.com/docs/en/api/models/retrieve)). It includes:
- `thinking.types.adaptive`;
- `effort`, with one entry per level;
- `context_management`, with one entry per strategy.

`ProviderEndpointScanner.ScanAsync` already sends `GET /v1/models` to every provider and recognizes Anthropic's list by its shape, so reading these records adds no request.

**Rule.** It applies to each candidate on `/v1/messages` that the router chose. That is every candidate except an explicitly named model's own first attempt (ADR-0005). Only that candidate's copy of the body changes.

| The request carries | Removed when the model's record says | Removed with it |
|---|---|---|
| `thinking` of type *T* | `thinking.types.T.supported` is false | Nothing: no beta value pairs with it |
| `output_config.effort` at level *L* | `effort.supported` is false, or `effort.L.supported` is false | Beta values that start with `effort-`, and `output_config` itself if nothing else is left in it |
| A `context_management.edits` entry of type *S* | `context_management.S.supported` is false, or *S* starts with `clear_thinking_` and this table removed `thinking` | `context_management` and beta values that start with `context-management-`, once no edit is left |

- **Unknown means unchanged.** The field is left as sent when the record is missing (no scan yet, or a list that isn't Anthropic-shaped), when `capabilities` is null, or when the key it needs is missing or null. That is today's behavior, and Claude Code's own recovery stays the backstop.
- **Only that candidate's copy changes** (ADR-0017 Strip rule 2). Today `BuildFallbackCandidate` derives a failover body from the primary's rewritten body. It must derive it from the unstripped body instead, so a capable fallback still gets thinking and effort.
- **History is left alone.** Earlier thinking blocks stay. The API drops blocks a model can't read, without an error.
- **Every strip is recorded** (ADR-0017 Strip rule 4). One Information log line per stripped request, with a static template and feature names from a fixed vocabulary. The `X-ArcRouter-Stripped-Features` response header carries the same names (choice 7).

**Work.** Before editing, verify these with CodeGraph: `RequestInterceptor`, `RoutingCandidateBuilder`, `RequestBodyIntrospection`, `RouteCandidate`, `UpstreamRequestBuilder`, `ProviderEndpointScanner` and `ToolCallCapabilityStore`. Do not change `ManagementFacade`'s public method set.

1. **Gate, before any code.** Run "Refresh from endpoint" on the `anthropic` provider, or one `GET /v1/models/claude-haiku-4-5-20251001`. Confirm that Haiku 4.5's record marks adaptive thinking and effort as unsupported.
   - If `capabilities` is null for Haiku 4.5, stop and report back. This phase would then fix nothing for Haiku.
   - The fallback would be the Observed tier (learning from 400s that name a field), which belongs to ADR-0017.
   - **Result, 2026-10-02: passed.** David ran `GET /v1/models/claude-haiku-4-5-20251001` and pasted the response. Its `capabilities` object is present:
     - `thinking.types.adaptive.supported` is false, and `thinking.types.enabled.supported` is true.
     - `effort.supported` is false, and every level, `xhigh` included, is an explicit `{"supported": false}` rather than null.
     - `context_management.supported` is true, with `clear_thinking_20251015` and `clear_tool_uses_20250919` true and `compact_20260112` false.

     So the rule removes `thinking` and `output_config.effort` from Haiku-bound copies. It also removes the `clear_thinking_20251015` edit, but only because thinking was removed; the record itself supports it. Whether Haiku would reject that edit without thinking is still undocumented, so removing it stays the safe side.
2. **Scanner.**
   - Parse `data[].capabilities` from Anthropic-shaped lists into a new `ModelFeatureSupport` record, keyed by provider and upstream model id.
   - Ignore fields the parser doesn't know. The live response already carries `line` (`"haiku"`), which the API reference doesn't list.
   - Match a record to a route by its exact upstream model id (`ProviderModelId`). The list names full ids only, so a route configured with an alias has no record, which means unchanged.
   - Read the whole list: `limit` up to 1000, then `has_more` and `after_id`. Today the scan reads only the first page, and Anthropic returns 20 models per page by default.
3. **Store.**
   - Persist the records in their own table (raw `capabilities` JSON, choice 4), replaced as a set on each scan of that provider. This needs a schema migration.
   - Expose them through a read interface shaped like `IModelContextWindowStore`.
4. **Stripper.** A pure static function over the parsed body and one record. It returns the features and the beta prefixes it removed.
5. **Candidates and headers.**
   - `RoutingCandidateBuilder` passes each route's record to `RequestBodyIntrospection`.
   - `RouteCandidate` gains one optional trailing member: the beta prefixes to drop.
   - `UpstreamRequestBuilder.CopyClientHeaders` rebuilds `anthropic-beta` without those values and forwards every other value unchanged.
6. **Operator note.** Add to `docs/install/harnesses/claude-code.md`: run "Refresh from endpoint" on the `anthropic` provider once after upgrading. Configured providers are scanned only when saved or on demand.
7. **Response header.** `RoutingResponseHeaders` gains `X-ArcRouter-Stripped-Features` for the candidate that answered (choice 7).
8. **GUI.** The admin contract's model view gains an optional capability record. The Providers tab's model rows show it as badges per `docs/gui/DESIGN.md`, with no badges when there is no record (choice 6).

**Tests** (each well under 5 s):

- **Stripper:**
  - Each table row.
  - An unsupported level, such as `xhigh` on a model without it.
  - Supported → unchanged. Unknown or null → unchanged.
  - `output_config.format` survives when only effort is removed.
  - Unrelated beta values survive.
  - `context_management` and its beta value go only when no edit is left.
  - No `thinking` field → nothing removed.
- **Scanner:**
  - An Anthropic-shaped list with capabilities.
  - `capabilities: null`.
  - A two-page list.
  - An OpenAI-shaped list records nothing.
- **Candidates:**
  - A stripped primary followed by an unstripped capable fallback.
  - An explicit pick is never stripped.
  - A path other than `/v1/messages` is never stripped.
- **Header:** present with the stripped names when a stripped candidate answers, and absent otherwise.
- **GUI:** a bUnit test per badge state: supported, unsupported, and no record (no badges).
- **Golden-path smoke** from the refactoring plan's validation gate, because `ProxyMiddleware`'s hot path changes.

**Proof for the pull request.** Re-run the 2026-10-02 capture with this chain: Claude Code → router → a localhost stand-in `anthropic` provider whose `/v1/models` serves a Haiku-shaped record. Show these:
- the forwarded helper and `Explore` bodies without the stripped fields;
- a capable model's body unchanged;
- the log lines and the `X-ArcRouter-Stripped-Features` header;
- the Providers tab badges for the stand-in Haiku record.

**Until this ships.** Stopping `claude-haiku-4-5-20251001` in Governance > Providers avoids the 400s. Helpers then go to the next-cheapest `anthropic` model.

**Decided (David, 2026-10-02: "approved, go with your recommendations on all three"):**

1. **Scope.** Strip on every router-chosen candidate on `/v1/messages`, not only on helper and light-subagent routes. The fault lies with the picked model, not the signal, and the same code closes the older unbiased exposure.
2. **ADR.** [ADR-0022 Amendment 1](../adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md#amendment-1-2026-10-02-strip-what-the-picked-model-rejects) adopts ADR-0017's Strip rules 1, 2 and 4 for these three fields. ADR-0017 absorbs it once accepted. (AGENTS.md requires an ADR before the router changes what it forwards upstream.)
3. **Storage.** The records are persisted, so they survive restarts as the endpoint record does.

> **Implementation choices (David, 2026-10-02, asked before coding).** These sharpen or depart from the text above.
>
> 4. **Record shape: raw JSON per model.** Each row holds Anthropic's `capabilities` object as JSON, keyed by provider and upstream model id. It sits in its own table, following ADR-0002's precedent for context windows. The stripper looks keys up by name, so a new strategy or effort level needs no migration.
> 5. **Freshness: operator note only.** There is no startup or periodic scan. Work step 6's note ("run Refresh from endpoint once") is the mechanism. Until a scan runs, nothing is stripped.
> 6. **GUI: full chain.** Each model row on Governance > Providers shows the record as badges (adaptive thinking, effort, context management). This adds an optional per-model field to the admin contract, the client mapping, the badges and bUnit tests, following `docs/gui/DESIGN.md`. It is recorded in ADR-0022 Amendment 1 rather than a separate ADR.
> 7. **Strip trace: log line plus response header now.** `X-ArcRouter-Stripped-Features` names the features removed from the candidate that answered. It is absent when nothing was stripped. This is ADR-0017's header, shipped early and recorded in Amendment 1. Telemetry and transcript columns still wait for ADR-0017.

**Risks:**

- **Stale records.** Scans run only on save or on demand. A model that gains a feature keeps losing it until the next scan. A model that loses a feature fails as it does today.
- **Fields the Models API doesn't describe aren't covered.** Examples: Opus 5.5 rejecting `thinking: {"type": "disabled"}`, or a strategy the record has no key for. Claude Code's recovery stays the backstop.
- **Removed effort means the model's default effort.** That is `high` on most models, and `medium` on Opus 5.5.
- **This phase pre-empts part of ADR-0017.** It is kept to the three fields above so ADR-0017 can absorb it without rework.

**Exit criterion:** no build warnings, all tests green, coverage at least 80%, and the gate-1 result recorded in this plan.

**Status, 2026-10-02:** met. The solution builds with 0 warnings, all 4,330 tests pass across the nine test projects, and the router test run covers 86.3% of lines (the new and changed files 97-100%, except `ProviderAdminGrpcService` at 64.7%, which is earlier code; its new mapping is covered). Gate 1 is recorded above, and the smoke run is recorded under "Where the build differs".

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

## Phase 4 — Full test matrix. **Done (2026-10-06).**

Most of the matrix already existed at the unit level when this phase started: `UtilityRoutingPolicyTests` and `CompositeRoutingPolicyTests` cover the near-best rule, `SubagentSignalDetectorTests` covers every Copilot alias spelling, and `RequestInterceptorSubagentBiasTests` covers the interceptor wiring against a stub policy. The gap was the end-to-end outcome with real policies, which `RequestInterceptorSubagentRoutingMatrixTests` now covers: a real interceptor over the real composite, utility policy, memory and circuit breaker, ending on a resolved model. It adds the circuit-open case, which had none, and the Copilot aliases through the interceptor. The strongest allowlist test (a policy returning a model that is not configured) was already `RequestInterceptorSubagentBiasTests.MarkedRequest_NeverResolvesOutsideTheConfiguredModels`; the new allowlist test only checks that real policies stay inside the list.

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

David, 2026-10-02, after the Haiku 4.5 capture:

7. Fold the fix for requests Haiku 4.5 rejects into #163 rather than a new issue. This is Phase 2c.
8. Prefer routing rules that are not pinned to specific agent versions where possible. Phase 2c reads this as "no model or harness version list in the rule".
9. Phase 2c is approved. It strips on every router-chosen candidate on `/v1/messages`, not only on biased routes.
10. The ADR route is an amendment to ADR-0022 that adopts ADR-0017's Strip rules 1, 2 and 4 for these fields.
11. The model capability records are persisted.
