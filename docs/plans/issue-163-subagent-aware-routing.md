# Plan: Subagent-aware routing (#163)

**Status:** Proposed. Awaiting David's approval. No implementation in this change.
**Issue:** [#163](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/163) — "P1: Subagent-aware routing (cheaper-model bias for harness subagent / side-task requests)".
**Standing rule:** [Approved plan before coding](../router/standing-rules.md#approved-plan-before-coding). The implementation pull request must link this plan once approved.
**ADR-0008 Amendment 1:** Binding. This plan adds a feature. It schedules no smell audit, splits no files, and refactors nothing beyond what the feature needs.
**Out of scope:** the semantic cache (#164) and every other P1/P2 item.

## Goal

When a harness marks a request as a subagent or narrow side task, route it with the existing cost-aware, quality-gated utility selection. When no verified signal is present, or the signal is malformed or ambiguous, routing must behave exactly as it does today.

## What exists today (from CodeGraph)

- `HeuristicRequestClassifier.Classify(JsonObject)` computes `RequestClassification(Dimension, Difficulty, Language, IsUtility)` from the **body only**. `InferIsUtility` fires on `max_tokens` ≤ 64 or a short prompt (≤ 200 chars) naming a helper task. It has no access to headers.
- `IRequestClassifier` has one caller (`RequestInterceptor.ResolveModelRouteAsync`, [`RequestInterceptor.cs:378`](../../src/TotallyHotArcRouter/Proxy/RequestInterceptor.cs)). `RequestInterceptor` holds the `HttpContext`, so headers are available at the call site.
- `RequestInterceptor.ResolveAgenticRouteAsync` builds `RoutingContext(Dimension, IsUtility, Candidates)` from the classification (`RequestInterceptor.cs:653`).
- `CompositeRoutingPolicy.SelectModelAsync` sends `context.IsUtility == true` to `UtilityRoutingPolicy`. That policy ranks by `ε₁·quality + ε₂·κ` (κ from `IModelPriceCatalog`), gates on `RoutingOptions.UtilityMinQualityScore`, and only ever picks from `context.Candidates`. The interceptor rejects any selection not in the candidate set, so the allowlist already holds.
- The routing decision is logged at `RequestInterceptor.cs:694` with `isUtility`. The Copilot aliases (`copilot-utility`, `copilot-utility-small`) already feed `IsUtility` through the same path.
- Blast radius of the changes below: `RequestClassification` (constructed by `HeuristicRequestClassifier`, `RequestInterceptor` tests, and `RequestTelemetryPublisher`), `RoutingContext` (67 references; **left unchanged**), `CompositeRoutingPolicy` (**left unchanged**).

## Design decisions (recommendations; flag any you disagree with)

1. **A small dedicated detector, not a classifier rewrite.** New `SubagentSignalDetector` takes the request headers plus the parsed body and returns `SubagentSignal?`. `IRequestClassifier` keeps its signature, so its test fakes and `HeuristicRequestClassifier` stay untouched.
2. **Reuse the utility path.** A detected signal sets `IsUtility = true` on the classification, so `CompositeRoutingPolicy` → `UtilityRoutingPolicy` runs unchanged. That gives the quality gate, price freshness, and allowlist for free, and avoids a second cost policy.
3. **Record the reason.** `RequestClassification` gains an optional trailing `SubagentSignal? Subagent = null`. Existing positional callers compile unchanged.
4. **Scope of the bias.** It applies only where a routing policy already runs (the `auto` / agentic alias). A request with an explicit model pick keeps ADR-0005 behavior: no silent reroute. Phase 1 confirms this by reading the code path; if explicit picks can reach the policy, the detector is gated off for them.
5. **Kill switch.** `Routing:SubagentBias:Enabled` (default **true**, decided by David; signals are verified before shipping) plus a per-signal allowlist.
6. **Fail-safe parsing.** Any exception, oversized header, non-UTF-8 value, or conflicting signals → `null` (no signal), logged at debug with a static template.

## Phase 1 — Signal research (no code)

**Deliverable:** a "Subagent and side-task signals" section in `docs/router/utility-model-routing.md`. It gets one row per harness: signal, where it appears (header, body field, model alias), source (doc link or captured request), verification status, and a detector decision (`implement` / `skip: no usable marker`).

Harnesses: Claude Code, Cursor, Codex, Aider, plus the existing Copilot aliases.

Method, in order of preference:
1. Official docs (fetched and linked with access date).
2. A captured real request through the router's own debug log (`[INTERCEPTOR]` body log is capped at 4,000 chars, so header capture needs a small temporary local script or `--export-ca` MITM, not a code change to the router).
3. Anything else is labelled **unverified** and is **not** implemented.

Starting hypotheses to test, not facts (I have not verified any of these):

| Harness | Hypothesis to verify |
|---|---|
| Claude Code | Background/side calls use the configured small-fast (Haiku-tier) model name, so the **model alias** is the signal. Subagent (Task tool) requests may differ by system-prompt shape or a session/agent header. Check current docs for gateway headers and `ANTHROPIC_DEFAULT_HAIKU_MODEL` semantics. |
| Cursor | Likely **no** documented subagent marker; may only distinguish by model or endpoint. |
| Codex | A subagent or session-source header may exist in recent CLI versions. Confirm against current source and docs before relying on it. |
| Aider | Weak-model (`--weak-model`) calls for commit messages and summaries arrive as a different **model name**. No header expected. |
| Copilot | Already covered by the `copilot-utility*` aliases. Document only. |

**Exit criterion:** every row is verified with a source, or explicitly marked "no usable marker". Rows that end as "skip" mean the detector ships without them; the issue explicitly allows that.

**Gate:** send the finished signal table to David before Phase 2. If Phase 1 finds no usable signal for a harness, that harness ships as documented "not supported".

## Phase 2 — Detector and classification plumbing

- Add `Router/Classification/SubagentSignalDetector.cs` (+ `SubagentSignal` record: `Harness`, `Kind`, `Source`). Pure function, no I/O, bounded work, fully XML-documented (CS1591 is an error here).
- Extend `RequestClassification` with the optional `Subagent` member.
- In `RequestInterceptor.ResolveModelRouteAsync`, after `_requestClassifier.Classify`, call the detector with `context.Request.Headers` and `jsonObject`. On a hit, replace the classification with `IsUtility = true` and `Subagent = signal`. No other change to the flow.
- Add `SubagentBiasOptions` under `RoutingOptions` (enable flag, per-signal toggles), bound from `appsettings.json`, documented.
- **Verify with CodeGraph before editing:** callers of `RequestClassification` construction (`HeuristicRequestClassifier`, `RequestTelemetryPublisher.cs:722`, tests) and the `RequestInterceptor` hub. Do not touch `ManagementFacade`.

**Exit criterion:** builds warning-free; existing tests unchanged and green.

## Phase 3 — Bias, log line, telemetry event, and dashboard

- The bias itself needs no new policy code: `IsUtility` already routes to `UtilityRoutingPolicy`. Phase 3 confirms that in a test rather than adding a parallel weight.
- **Log line.** Extend the routing log at `RequestInterceptor.cs:694` with `{SubagentSignal}` (static template, sanitized via `SanitizeForLog`). No-signal requests log `none`.
- **Telemetry event (David chose "diary and scoreboard").** Carry the signal from the classification to `RoutingTelemetryEvent` (`Telemetry/RoutingTelemetryEvent.cs`), published by `RequestTelemetryPublisher.PublishTelemetryEventAsync`. Add one nullable, trailing member (for example `SubagentSignal`) so existing positional constructors (32 callers, mostly tests) still compile.
- **Wire and dashboard.** The event crosses gRPC (`ToWire`) to the GUI. Add the matching optional proto field and show it in the Live Stream view (`LiveStream` under `Dashboard`). Follow `docs/gui/DESIGN.md` for the badge and `docs/gui/MOTION.md` if it animates; add a bUnit test for the badge. Missing field (older router) renders as nothing.
- **ADR check.** AGENTS.md says transport changes get a new ADR first. An additive, optional field on the loopback telemetry stream is small, but I will ask you in the Phase 3 gate whether it needs one before I touch the proto.

**Exit criterion:** a signalled request's log line, telemetry event, and Live Stream row all name the signal and the chosen model; an unsignalled one shows none.

## Phase 4 — Tests

Follow `RequestInterceptorRoutingPolicyTests` and `CompositeRoutingPolicyTests`; each test stays well under the 5 s ceiling.

- **Detector unit tests** (`SubagentSignalDetectorTests`): one test per verified signal (present → detected), header absent, empty, oversized, wrong case, duplicate/conflicting values, non-JSON body → `null`.
- **Interceptor tests**: signalled request with a priced cheap and an expensive candidate → cheap model chosen; same request without the signal → identical result to a baseline captured before the change (assert the exact model and that `IsUtility` is false).
- **Quality gate**: cheapest candidate below `UtilityMinQualityScore` is skipped even when signalled.
- **Allowlist**: signalled request never resolves outside `ListModels()`; a circuit-open cheap model is not selected.
- **Explicit model pick** with a signal present → unchanged (ADR-0005).
- **Ambiguous/malformed signal** → existing routing.
- **Kill switch** off → signal ignored.
- **Regression**: existing `copilot-utility*` alias and payload-heuristic tests untouched and green.
- Coverage on the new code ≥ 80%.

## Phase 5 — Docs and proof

- Update `docs/router/utility-model-routing.md`: signal list (from Phase 1), detection, bias, fallback, config keys.
- Link from `docs/research/technical-reference.md` E.3 ("Sub-agent routing") to that section.
- PR body proof, as hosted artifacts: `dotnet test` output, and a real or replayed request pair (signalled → cheaper model, unsignalled → unchanged) with the log lines. Use the replay path against local fixtures if a live harness capture is not available, and say which it is.
- Update `docs/install/harnesses/*.md` only where a harness needs a setting for its signal to reach the router (for example, naming the small-fast model alias). Do not rework presets (out of scope).

**Exit criterion:** full build and test suite green, no warnings, the checklist in the issue's "Done when" ticked with links.

## Risks

- **Signals may not exist.** If a harness sends no marker, that harness ships as "not supported"; we do not invent one. The issue accepts this.
- **Forged or misleading headers.** A client can send any header, but the only effect is a cheaper model within the operator's own allowlist and quality gate, so this is a cost/quality preference, not an authorization boundary. Stated in the docs.
- **Subagents doing hard work.** A real subagent may be a difficult coding task. The quality gate only excludes models with *observed* low scores. Mitigation: per-signal toggles, and the difficulty field is left visible in the log so it can be tuned later. Not solved by this plan.
- **Wider blast radius from the dashboard choice.** Adding a telemetry field touches `RoutingTelemetryEvent` (32 references), the proto, and the GUI. Kept additive and optional so no existing caller changes.
- **Header volume.** The detector reads a few named headers only; no header enumeration on the hot path.

## Decisions (David, 2026-09-29)

1. `Routing:SubagentBias:Enabled` defaults to **on**. Only verified signals ship, and the kill switch stays.
2. A harness with no verifiable marker is **documented as unsupported**. No guessing from payload shape.
3. Telemetry: **log line and dashboard**. The signal goes into `RoutingTelemetryEvent`, the gRPC wire, and the Live Stream view.

Still open: whether the additive proto field needs its own ADR (asked at the Phase 3 gate).
