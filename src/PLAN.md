# Current Implementation Plan: Measuring and Extending the Live C-A-F Loop

This plan tracks **unfinished work only**. Shipped phases are not narrated here; each has an owning
doc in the table below. Do not re-open a shipped row without new evidence.

**Objective.** Bring the running router to the architecture in
[`../docs/research/technical-reference.md`](../docs/research/technical-reference.md) — a loop-complete
**C-A-F** router (Context → Action → Feedback → Context) selecting under `r = ε₁·s + ε₂·κ` and
measured by cumulative regret against a per-task oracle.

## Already shipped (owning docs)

| Shipped work | Owning doc |
|---|---|
| H, I — classifier, `IRoutingPolicy`, cost-aware utility routing | [`utility-model-routing.md`](../docs/router/utility-model-routing.md) |
| G, J — feedback loop, `RouterMemory` + `EmbeddingMemory` | [`memory-persistence.md`](../docs/router/memory-persistence.md) |
| K, K2 — CodeRouterBench in SQLite | [`coderouterbench-sqlite-migration-plan.md`](../docs/router/coderouterbench-sqlite-migration-plan.md), [`data/README.md`](../data/README.md) |
| L — Orchestrator ensemble (five voters) | [`orchestrator-ensemble.md`](../docs/router/orchestrator-ensemble.md) |
| M, M1–M4 — Orchestrator on the live path | [`orchestrator-live-path-plan.md`](../docs/router/orchestrator-live-path-plan.md) |
| Live-feedback 1–5 — capture, embedding `logreg`, Governance admin | [`live-feedback-learning-plan.md`](../docs/router/live-feedback-learning-plan.md) |
| Routing ROI vs the frozen untrained baseline ([`score-delta-methodology.md`](../docs/score-delta-methodology.md)) | [`self-organizing-classification-plan.md`](../docs/router/self-organizing-classification-plan.md) (T4) |
| T1–T6 — transcripts, clustering, `cluster_best`, adaptive-routing toggle | [`self-organizing-classification-plan.md`](../docs/router/self-organizing-classification-plan.md) |
| N1–N6 — regret harness (measured; **exit criterion not met**) | [`regret-evaluation-harness-plan.md`](../docs/router/regret-evaluation-harness-plan.md) |
| Q0–Q4 — quality rescan, keyed graders, portfolio, CLI reliability report | [`quality-verifier-architecture.md`](../docs/router/quality-verifier-architecture.md), [`grader-reliability-plan.md`](../docs/router/grader-reliability-plan.md) |
| G1–G3 — shadow judge, calibration check, judge blended into `u_i` | [`geval-shadow-scoring-plan.md`](../docs/router/geval-shadow-scoring-plan.md) |
| Sessions tab persisted transcripts | [`../docs/gui/dashboard.md`](../docs/gui/dashboard.md) |
| Auto-update detect (apply is manual: the dashboard links to the release) | [`packaging-and-distribution.md`](../docs/router/packaging-and-distribution.md), [`version-compatibility.md`](../docs/router/version-compatibility.md) |

The Verifier is static analysis + the G-Eval judge; **code execution was removed** (no sandbox, no
`Process` in `TotallyHotArcRouter.Quality`). Roslyn (C#) and Acornima (JS/TS) give authoritative
syntax verdicts; Python and shell use language-aware heuristics marked non-authoritative and weighted at
half. Every LLM-grader prompt carries the user's question as a required section, and a missing question
fails closed (GitHub issue #114). Full design:
[`quality-verifier-architecture.md`](../docs/router/quality-verifier-architecture.md).

## Remaining work

1. **Q5 — sample-size-aware `DimBestVoter`.** Evidence-blocked: live `RouterMemory` holds zero
   observations here, and the offline harness hands `dim_best` an empty memory, so a live-vs-prior
   blend is invisible to the only measurement surface. Re-open condition and paired-online-arm design:
   [`regret-evaluation-harness-plan.md`](../docs/router/regret-evaluation-harness-plan.md) Q5.
2. **Ensemble vs DimensionBest, for real.** N5 measured a tie with `dim_best` (and a bandit winning
   on OOD). Closing the *substance* needs a live-traffic regret arm or a richer offline bootstrap for
   the three excluded voters. **Unscheduled.**
3. **Embedding re-key after a model change.** `memory_entries` stores vectors without prompt text;
   superseded-model rows are filtered until they age out. A background re-embed over linked
   transcript rows is unscheduled. Provenance for detecting the condition shipped with
   `live-feedback-learning-plan.md`.
4. **Cost term on the general (non-utility) live path.** `UtilityRoutingPolicy` prices candidates;
   the Orchestrator does not. Unscheduled.
5. **Live-feedback Phase 6 remainder** — namespace relocation of TF-IDF/`LogRegTrainer` types
   (`live-feedback-learning-plan.md`). Placeholder deletion already shipped.
6. **Counterfactual token estimation Phases 4–6** —
   [`counterfactual-token-estimation-plan.md`](../docs/router/counterfactual-token-estimation-plan.md)
   (closes when Phase 5 ships GUI confidence surfacing).
7. **Admin-slice golden-path smoke** — implementation shipped; the stated end condition still wants
   the manual smoke
   ([`admin-slice-consolidation-plan.md`](../docs/router/admin-slice-consolidation-plan.md)).
8. **Code-smell plan golden-path smoke** — Critical extracts shipped; the plan does not close until
   the hot-path smoke runs
   ([`code-smell-refactoring-plan.md`](../docs/router/code-smell-refactoring-plan.md)).

## Other open work (tracked elsewhere)

- [`../docs/router/tracked-todos.md`](../docs/router/tracked-todos.md) — #3 DeepSeek dialect, #5
  human review of tool-call-normalization Phase 5, #6 Gemini cost reconciler.
- [`../docs/router/tool-call-normalization.md`](../docs/router/tool-call-normalization.md) — Phase 6
  remainder (response/telemetry diagnostics), Phase 7 (native endpoints, design only).
- [`../docs/gui/backlog.md`](../docs/gui/backlog.md) — remaining live-telemetry gaps (Routing ROI /
  Tool Steps / Context Buffer, deliberately mock-backed) and
  [`governance-model-cards.md`](../docs/gui/governance-model-cards.md)'s missing model price channel.
- [`../docs/router/agent-resilience-strategies.md`](../docs/router/agent-resilience-strategies.md) —
  Leaky Bucket (pattern 2) not yet built.
- Proposed, unscheduled:
  [`security-hardening-plan.md`](../docs/router/security-hardening-plan.md),
  [`proxy-coexistence.md`](../docs/router/proxy-coexistence.md),
  [`system-proxy-architecture.md`](../docs/router/system-proxy-architecture.md).

## Settled deferrals (do not re-open without new evidence)

- **Q4 gRPC/Governance panel** — CLI `--run-grader-reliability-report` already measures; GUI would
  add no measurement capability.
  [`grader-reliability-plan.md`](../docs/router/grader-reliability-plan.md).
- **G2 self-preference is numbers, not pass/fail** — no unbiased control group.
  [`geval-shadow-scoring-plan.md`](../docs/router/geval-shadow-scoring-plan.md).
- **G3 gate condition (1) permanently `Unevaluable`** — required execution-grounded scores; execution
  was removed. Same doc.
- **Quality rescan does not write router memory** — a second writer would double-count.
  [`quality-verifier-architecture.md`](../docs/router/quality-verifier-architecture.md) §3.3.
- **Multimodal price tiers** — no upstream `resolution_tier` feed.
  [`model-price-catalog.md`](../docs/router/model-price-catalog.md).
- **Routing ROI / Tool Steps / Context Buffer GUI metrics** — mock-backed until the domain concepts
  exist. [`../docs/gui/backlog.md`](../docs/gui/backlog.md).
- **Reasoning-token pricing** — `UsageInfo.ReasoningTokens` has no matching price column.
- **CodeRouterBench `outputs/`, `agentic-artifacts/`, nested `raw_matrices/`** — not restored.
  [`data/README.md`](../data/README.md).
- **Exact per-cell Table 10 parity** for GLM-5 / Qwen3-Max / Qwen3.5-Plus / MiniMax-M2.7 — settled as
  upstream judge noise. Same.
- **`llm_router` uses an off-the-shelf model**, not the paper's unpublished checkpoint.
  [`orchestrator-ensemble.md`](../docs/router/orchestrator-ensemble.md).
- **Named-model requests are never routed** — a servable name is a command.
  [`orchestrator-live-path-plan.md`](../docs/router/orchestrator-live-path-plan.md) §1.
- **G1 auto-CoT is a static prompt constant**; n-sample fallback is a single numeric parse.
  [`geval-shadow-scoring-plan.md`](../docs/router/geval-shadow-scoring-plan.md).
- **Judge backbone is a Providers-screen free model**, not a hardcoded local endpoint. Same.

## Final Validation Gate

Applies at the end of every phase, per [`../AGENTS.md`](../AGENTS.md):

1. `dotnet build` passes with zero warnings and zero errors (`TreatWarningsAsErrors` is on repo-wide).
2. Every new public/protected type and member carries accurate XML documentation; docs on code changed
   by a phase are re-read for staleness, which the compiler cannot check.
3. All unit tests pass; both non-GUI assemblies hold ≥ 80% line coverage per-assembly, as
   `.github/workflows/dotnet-ci.yml` measures it.
4. No unusually heavy test exceeds 5 seconds.
5. Every routing decision is logged through Serilog with a **static** message template and structured
   properties.
6. Documentation matches delivered behavior — including `README.md` and `data/README.md` (`docs/HANDBOOK.md`
   is a pointer to those). `outputs/` / `agentic-artifacts/` stay unrestored unless a phase needs them.
7. Any item deferred during a phase is recorded with its evidence in the owning doc, and summarized
   under "Settled deferrals" above.
