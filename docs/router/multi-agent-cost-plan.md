# Multi-agent cost plan: free version first, enterprise second

**Status:** Proposed, 2026-09-30. Nothing here is boarded. Under the [standing rule of 2026-09-28](standing-rules.md#approved-plan-before-coding), each item below needs its own `docs/plans/issue-<N>-<slug>.md` plan approved by David before coding starts. [ADR-0008 Amendment 1](../adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md#amendment-1-2026-09-02-stop-rules) also binds: no item may include a refactor unless it cites an observed cost. Structural change is allowed only where a feature needs it.

**Sources:** "Arc Router vs. the field: marketing comparison (Sep 25, 2026)" (below, "the write-up"), the license research of 2026-09-25 ("the license research"), and this repository's `main` branch as of 2026-09-30. Neither research document is in the repo. Any competitor fact that isn't in the write-up is marked as such.

## Product decisions this plan builds on (David, 2026-09-30)

1. **Two versions.** The free public version lets a developer run several agents side by side at no cost. The paid enterprise version lets teams share routing data.
2. **Only enterprise is paid.** Every single-machine cost saving ships in the free version.
3. **Enterprise runs on-premises by default.** Customers host and control their own routing data. An optional paid add-on, for a small fee, has David host and process that data.
4. **Enterprise is closed source. The public version stays AGPL-3.0** ([LICENSE](../../LICENSE)).
5. **The free version launches first.** Enterprise work starts after the free-version cost wins ship.

## 1. Where Arc falls short on cost for multi-agent use

**Verified** means the gap is stated in the repo or the write-up. **Inferred** means it needs data before anyone acts on it.

| # | Gap | Evidence | Competitor contrast (write-up) |
|---|---|---|---|
| G1 | **Main-agent routing ignores cost.** Only helper requests are picked on price. | Verified. [how-it-learns.md](../how-it-learns.md): "The live general vote is cost-blind"; cost enters selection only in `UtilityRoutingPolicy`. The write-up says the same thing and sells it as a strength. | Not Diamond: "Frontier quality at a fraction of the price". OpenRouter routes by market spend. |
| G2 | **Claude Code and Codex can only route within their own dialect.** `auto` can pick a backend that can't read the request, and the turn fails. | Verified. [claude-code.md](../install/harnesses/claude-code.md) and [codex.md](../install/harnesses/codex.md) Limitation sections; [ADR-0017](../adr/0017-pin-auto-routed-requests-only-when-they-carry-backend-specific-features.md). Pinning waits on [tracked-todos #8](tracked-todos.md#8-capture-and-analyze-real-claude-code-and-codex-traffic-before-deciding-adr-0017s-pin-policy). | OpenRouter: "huge catalog, fallbacks". |
| G3 | **Subagent and side-task calls aren't detected**, and two presets throw away the signal. | Verified. [#163 plan](../plans/issue-163-subagent-aware-routing.md): "Nothing detects a real subagent". [aider.md](../install/harnesses/aider.md) sets `weak-model: openai/auto`, which erases the weak-model name that #163 hypothesizes as Aider's signal. The Claude Code preset sets only `ANTHROPIC_MODEL`. Claude Code documents `CLAUDE_CODE_SUBAGENT_MODEL` and `ANTHROPIC_DEFAULT_HAIKU_MODEL` (background work) ([env vars](https://code.claude.com/docs/en/env-vars), checked 2026-09-30). | Not Diamond brands itself "Model Routing for Coding Agents". |
| G4 | **The semantic cache (#164) probably misses most agent traffic.** | Verified rule: [semantic-cache.md](semantic-cache.md) never caches streamed requests or requests with tools. Inferred effect: coding harnesses usually stream and carry tools, so the hit rate is likely near zero. #8 should measure this. | None in the write-up. |
| G5 | **Routing ignores provider prompt caching.** Switching models per request can forfeit cached prefixes. | Verified: [utility-model-routing.md](utility-model-routing.md) B3 says `PriceContext` is always `Standard`. Claude Code's docs say "each model has its own prompt cache, so the first request after a switch re-reads the whole conversation uncached" ([settings](https://docs.anthropic.com/en/docs/claude-code/settings), checked 2026-09-30). Inferred: how much this costs Arc depends on real switching rates. ADR-0017 Option 5 (stickiness) waits on #8. | None in the write-up. |
| G6 | **Budgets stop at the provider.** No cap exists per session, per harness or per agent. | Verified. Provider caps are persisted and enforced. `ProviderBudgetStore`, the `IBudgetEnforcer` the request path calls, holds dollar and token caps per provider on a monthly, weekly or rolling-hour window. `ProxyMiddleware`'s budget gate skips a breached provider and returns 402 `budget_exhausted` when every candidate is breached ([token-tracking-improvements.md](token-tracking-improvements.md) lists this as implemented). Nothing finer exists: `usage_ledger` records `session_id` but not the harness, and the `IsWithinBudgetAsync` check in [agent-cost-tracking.md](agent-cost-tracking.md) §5 is a design sketch with no code behind it. | LiteLLM/Portkey: "Self-hostable proxy, budgets". |
| G7 | **Savings are reported per request, not per agent session.** | Verified: Routing ROI is a per-request estimate that skips turns with an unknown baseline ([score-delta-methodology.md](../score-delta-methodology.md); [ADR-0009](../adr/0009-per-request-counterfactual-token-estimation.md) is still *proposed*). Also verified: no per-session or per-harness rollup exists. `usage_rollup` buckets spend by time, provider and model only, and `usage_ledger` has no harness column. | Not Diamond shows "20% cost savings" and an ROI calculator. |
| G8 | **Utility routing is under-tuned.** | Verified: [utility-model-routing.md](utility-model-routing.md) B3 has no per-tier weights and no exploration. Follow-up 7 says utility quality "stays cold-start indefinitely" without a feedback signal. | None in the write-up. |
| G9 | **Gemini cost is never reconciled** against the provider's own bill. | Verified: [tracked-todos #6](tracked-todos.md#6-build-a-real-iprovidercostreconciler-for-gemini). | None in the write-up. |
| G10 | **Every machine learns alone,** so each developer starts cold. There is no team door. | Verified: the write-up ("'For teams' tier planned", "enterprise packaging is still thin"; the learning record is in local SQLite). | Not Diamond: SOC 2, SLAs. OpenRouter: network effects. |

**The write-up is out of date in two places.** Its "honest gaps" section says graders don't read the question and that Python/shell parsing is weak. Issues [#114](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/114) and [#115](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/115) closed both. Parsing verdicts for Python and shell are still weighted at half ([how-it-learns.md](../how-it-learns.md)).

## 2. Improvements

**Measurement.** Every free item reports against the same metric: **estimated spend saved per agent session versus the frozen baseline.** The frozen baseline is the untrained/`dim_best` counterfactual from [score-delta-methodology.md](../score-delta-methodology.md). Each result has a guardrail: the session's score delta (Δs) must not fall below a tolerance (see decision D2). Results must also be labelled estimates until they are reconciled.

### Free version (AGPL)

**F1. Per-session savings receipt.** Effort M.
- What: roll `usage_ledger` and the ROI counterfactual up by session and harness, reusing `SessionIdResolver` and the #111 dashboard.
- Attribution it has to add: the ledger already records `session_id`, but nothing records which harness sent a request. F1 adds that, for example from the request's `User-Agent` or a header the #155 presets set. The item's plan picks the source and confirms it with a capture. F5's caps reuse the same attribution.
- Saves money by: showing where the money goes, so the other items can be judged.
- Success: the receipt covers ≥ 90% of turns with a known baseline and appears per session in the dashboard.
- Why first: every later metric depends on it.

**F2. Harness presets v2 with distinct helper aliases.** Effort S.
- What: add router aliases such as `auto-small` and `auto-subagent`. Point Claude Code's `ANTHROPIC_DEFAULT_HAIKU_MODEL` / `CLAUDE_CODE_SUBAGENT_MODEL` and Aider's `weak-model` at them.
- Saves money by: giving #163 a verified model-alias signal instead of a guess, so helper and subagent calls take the cheaper utility path.
- Success: share of subagent/helper requests routed through `UtilityRoutingPolicy`, and saved spend per session on those requests.
- Timing: extends #163 Phase 1 findings. Don't rework the #155 presets beyond this.

**F3. Utility tier weights and a utility feedback signal.** Effort S–M.
- What: close follow-up 7 and the tier narrowing in [utility-model-routing.md](utility-model-routing.md).
- Saves money by: routing the smallest calls to the cheapest tier, with a live quality gate instead of a permanently cold one.
- Success: cost per utility request falls against the baseline while utility Δs stays inside tolerance.

**F4. Gemini reconciler (tracked-todos #6).** Effort M.
- Saves money by: making Gemini routes trustworthy enough to count in savings claims.
- Success: the daily estimated-versus-reconciled delta is reported. As #6 requires, the item states that it has not been tested end to end against real GCP billing.

**F5. Session budgets that downgrade `auto` instead of blocking.** Effort M.
- What: add per-session and per-harness caps next to the provider caps that already exist. Reuse `BudgetWindow` and the budget gate in `ProxyMiddleware` instead of building a second enforcer, and keep the check an in-memory read like `IsBreached`, not a ledger query per request. Spend per session and per harness comes from F1's attribution. As a cap nears, `auto` routes lean on the utility policy instead of being blocked the way a breached provider is today. A client that names an explicit model is never overridden ([ADR-0005](../adr/0005-protect-explicit-provider-selections-from-silent-substitution-on-any-circuit-trip.md)).
- Saves money by: capping runaway multi-agent spend.
- Success: zero sessions exceed their cap without an alert, and Δs on downgraded turns is reported.

**F6. Cost-aware main vote (opt-in "savings mode").** Effort M.
- What: apply the utility policy's `ε₁·s + ε₂·κ` ranking with its quality gate to the agent path. It is off by default until F1 data exists. [how-it-learns.md](../how-it-learns.md) calls this "a change in behaviour", so it needs an ADR.
- Saves money by: closing G1 directly.
- Success: ≥ X% estimated spend saved per session against the frozen baseline with Δs inside tolerance. David sets X after F1 produces a baseline; no target is invented here.

**F7. Cache-aware stickiness.** Effort M.
- What: set `PriceContext.RepeatsCachedContext` from session continuity, and charge a mid-session switch for the cached prefix it forfeits (ADR-0017 Option 5).
- Saves money by: avoiding the cache re-reads described in G5.
- Success: cache-read token share per session rises and cost per session falls. It waits on #8's persistence analysis.

**F8. Inbound translators (Messages, then Responses).** Effort L.
- What: build a translator only where the #8 census gives P < 90%, per ADR-0017's rule. Each translator gets its own ADR.
- Saves money by: widening Claude Code and Codex routing beyond one vendor (G2).
- Success: share of harness turns routed off-dialect without failures.

**F9. Extend the semantic cache to agent traffic, only if data supports it.** Effort M.
- What: use #8 and #165 data to measure how many requests could be served. Keep #164's rules and don't rebuild the cache.
- Saves money by: serving repeat requests without a provider call.
- Success: hit rate on agent sessions, with zero reported false hits.

**Positioning (free).**
- Change the headline from "doesn't pick; it grades" to "it grades, then shows what each agent session saved."
- Keep the "estimate, not ROI" honesty until reconciliation covers the route.
- Correct the stale gaps in the write-up.
- Lead against Not Diamond's savings claim with per-session receipts on the user's own traffic.

### Enterprise version (closed, paid)

**E1. Shared routing data, on-premises.** Effort L.
- What: a customer-hosted team hub that pools the learning record across a team's machines: route, cost, grade, dimension, and memory tallies.
- Saves money by: removing per-developer cold start (G10), so one team's grades inform everyone's routing.
- Success: time-to-baseline for a new seat, and per-session savings for new seats compared with isolated seats.
- Boundary: no prompts or bodies leave a machine by default. #165's archive stays operator-local. Whether embeddings are shared is decision D5.

**E2. Team governance.** Effort M.
- What: team-level budgets and dashboards that aggregate F1 and F5 across seats and teams.
- Success: team spend saved against the frozen baseline, per team and per harness.

**E3. Hosted add-on.** Effort L.
- What: David hosts and processes the E1 data for a fee.
- Why last: it adds a new place for data to go, which undercuts the write-up's "no new place for your prompts" pitch. Keep it scoped to the learning record only.
- Success: the attach rate among enterprise customers.

**Pricing and packaging.**
- Free: $0, with every single-machine saving included.
- Enterprise: a paid license for the hub and team features, plus the hosted add-on.
- No price numbers are proposed here. The write-up has no competitor prices to anchor against.

## 3. Open core boundary, licensing and contributions

This section is not legal advice. The license research asks for a lawyer's review before any CLA or commercial terms go out.

**Where the line sits.**
- **AGPL core:** everything that runs on one machine, including F1–F9, all harness presets, the price catalog, the ledger, grading, and learning.
- **Closed enterprise:** multi-seat sync, the team hub, team governance, and hosting.
- **Rule:** no feature moves from free to enterprise.
- **How the code connects:** the core exposes a documented extension seam, for example a pluggable routing-memory sync interface. That seam is justified by E1, not by code smell, which satisfies Amendment 1. Enterprise lives in a separate private repository.

**What AGPL means here.** If closed enterprise code ships combined with the AGPL core, it may count as a derivative work. That combination works only because David, as sole copyright holder, can grant the core under commercial terms to enterprise customers. The license research found no non-owner human contributors, which makes that possible today.

**Contributions.**
- Adopt an Apache-ICLA-style CLA with a CLA bot, plus a `CONTRIBUTING.md` that states the open-core intent, **before the first outside pull request lands.** The license research explains that a DCO alone doesn't let outside contributions be sold under commercial terms.
- Enterprise code takes no outside contributions.

**Known risks** (from the license research):
- Released AGPL versions stay AGPL forever.
- The copyright status of AI-authored code is uncertain.
- Non-commercial OSS perks (JetBrains, Docker DSOS, Netlify) are lost once the paid tier exists.

## 4. Sequencing

| Phase | Items | Builds on | Gate |
|---|---|---|---|
| 0: in flight (don't rebuild) | #164 semantic cache (closed as completed 2026-09-28, 10:03 PM PT); #163 subagent routing; #165 archive ([plan](../plans/issue-165-export-import-history.md), awaiting approval); #8 census; presets #155 | Utility routing, price catalog, `usage_ledger`, ROI | Plans already approved or pending |
| 1: free wins that need no traffic data | F1, F2, F3, F4 (#6), positioning fixes, CLA and `CONTRIBUTING.md` | F2 on #163 Phase 1; F1 on ADR-0009 and #111 | One approved plan per item |
| 2: free wins gated on data | F5, F6 (ADR), F7, F8 (ADR per dialect), F9 | #8 census for F7, F8 and F9; #165 corpus for F9 tests; F1 baseline for F5 and F6; F1 harness attribution for F5 | #8 minimums met; F1 has ≥ 2 weeks of sessions |
| 3: free launch | Public launch with per-session receipts | Phases 1–2 | Savings stable against the baseline |
| 4: enterprise on-premises | Extension seam in core, then E1 and E2 | F1, F5 ledger; #165 zip conventions for manifests only | Free launch shipped; lawyer review; ADR for the seam |
| 5: hosted add-on | E3 | E1 | Security/compliance posture decided (D7) |

**What waits for real traffic:** F6's weights, F7, F8, F9, and the per-tier weights in F3. These need #8's census and F1's receipts. #163's quality gate only excludes models with observed low scores, so a hard subagent task routed cheap is watched in F1 before any default changes.

## 5. Open decisions for David

| # | Decision | Recommended default |
|---|---|---|
| D1 | Which frozen baseline the headline metric uses | The existing untrained/`dim_best` counterfactual; accept ADR-0009 first |
| D2 | Quality tolerance for "savings" | Session Δs ≥ −0.02, revisited after F1 data |
| D3 | Should F6 savings mode ship on or off | Off by default in the first release, on once F1 shows savings with Δs inside tolerance |
| D4 | Helper alias names for F2 | `auto-small` and `auto-subagent`, both advertised in `/v1/models` |
| D5 | Does E1 share embeddings, or only route/cost/grade | Route/cost/grade and tallies only; embeddings behind an opt-in per team |
| D6 | Can AGPL-averse enterprises get the core under commercial terms | Yes. The enterprise license covers core plus closed modules |
| D7 | Hosted add-on scope and compliance | Learning record only, no prompts; ship E3 only after a stated security posture |
| D8 | CLA form and rights holder | Apache-ICLA-style CLA, with rights held by a TotallyHot entity (lawyer to confirm) |
| D9 | Enterprise price unit | Per team or seat, set after the first design partner. No number until then |

## Not verified

- #164's pull request number and merge commit. Issue #164 closed as completed and [semantic-cache.md](semantic-cache.md) is on `main`, but GitHub's API rate limit blocked the pull request lookup.
- Nancy's 2026-09-20 backlog itself isn't in the repo. Its tiers here come from issues #111–#116, #163 and #164.
- How often agent traffic streams or carries tools, and how often routing switches models mid-session. #8 measures both.
- Whether the Claude Code variables above actually reach the router under a custom base URL. That needs a capture.
- Whether each harness sends a `User-Agent` that identifies it, which F1's harness attribution would rely on. The same capture can answer it.
