# Current Implementation Plan: Measuring and Extending the Live C-A-F Loop

This plan tracks only unfinished work. Completed phases are removed rather than archived; the
narrative for anything already shipped lives in the design doc that owns it (see the pointer table
below).

**Objective.** Bring the running router to the architecture described in
[`../docs/research/technical-reference.md`](../docs/research/technical-reference.md) — a
loop-complete **C-A-F** router (Context → Action → Feedback → Context) built from an **Orchestrator**,
a **Verifier**, and a **Memory**, selecting under the cost-aware reward
`r = ε₁·s + ε₂·κ` and measured by cumulative regret against a per-task oracle.

## Where things stand

**The C-A-F loop is closed and live.** The classifier (Phase H), the cost-aware `IRoutingPolicy` seam
(Phase I), the embedding memory (Phase J), the benchmark corpus in SQLite (Phases K/K2), the
Orchestrator ensemble (Phase L — four voters, joined later by a fifth, `cluster_best`, from Phase T3),
the Orchestrator on the live path with requested-vs-routed telemetry (Phase M, M1–M4), live feedback
capture plus the embedding-backed `logreg` trainer and its Governance admin surface
(`live-feedback-learning-plan.md` Phases 1–5), Routing ROI's expense-and-regret comparison
(`routing-roi-regret-plan.md`), self-organizing request classification end to end
(`self-organizing-classification-plan.md` Phases T1–T6), and five of the regret harness's six sub-phases
(`regret-evaluation-harness-plan.md` N1–N5) have all shipped. Narratives:

| Shipped work | Owning doc |
|---|---|
| Phases H, I — classifier, `IRoutingPolicy`, cost-aware utility routing | [`../docs/router/utility-model-routing.md`](../docs/router/utility-model-routing.md) (§B1–B5 status blockquotes) |
| Phases G, J — feedback loop reconnection, `RouterMemory` + `EmbeddingMemory` persistence | [`../docs/router/memory-persistence.md`](../docs/router/memory-persistence.md) |
| Phases K, K2 — CodeRouterBench synced into SQLite | [`../docs/router/coderouterbench-sqlite-migration-plan.md`](../docs/router/coderouterbench-sqlite-migration-plan.md), [`../data/README.md`](../data/README.md), [`../docs/router/model-identity-canonicalization.md`](../docs/router/model-identity-canonicalization.md) |
| Phase L — the Orchestrator ensemble (four voters; `cluster_best` added as a fifth by Phase T3) | [`../docs/router/orchestrator-ensemble.md`](../docs/router/orchestrator-ensemble.md) |
| Phase M (M1–M4) — Orchestrator on the live path, requested-vs-routed end to end | [`../docs/router/orchestrator-live-path-plan.md`](../docs/router/orchestrator-live-path-plan.md), [`../docs/router/phase-m2-plan.md`](../docs/router/phase-m2-plan.md) |
| Live-feedback Phases 1–5 — importer repair, feedback capture, embedding-backed `logreg`, training, Governance admin surface | [`../docs/router/live-feedback-learning-plan.md`](../docs/router/live-feedback-learning-plan.md) |
| Routing ROI — regret vs `dim_best`, one-minute full drain, hard pause under load | [`../docs/router/routing-roi-regret-plan.md`](../docs/router/routing-roi-regret-plan.md), [`../docs/router/self-organizing-classification-plan.md`](../docs/router/self-organizing-classification-plan.md) (Phase T4 status block) |
| Phases T1–T6 — transcript capture, self-organizing clustering, `cluster_best` voter, baseline comparison, Cluster Model admin pane, System Settings adaptive-routing toggle | [`../docs/router/self-organizing-classification-plan.md`](../docs/router/self-organizing-classification-plan.md) |
| Phase N (N1–N5) — regret metrics core (`RegretReplayResult`), the no-leakage streaming replay engine (`RegretReplayEngine`), all six comparison baselines (Always-*m* / DimensionBest / LinUCB / LinTS / kNN Retrieval / LogReg), and the Orchestrator arm + comparison report (measured; exit criterion not met — see below) | [`../docs/router/regret-evaluation-harness-plan.md`](../docs/router/regret-evaluation-harness-plan.md) (N1–N5 status notes) |
| Phase Q0 — quality rescan over saved task data (`QualityRescanService`, `scorer_version` column, `Quality:ScorerVersion`, prompt carried onto `QualityRequest`), off by default | [`../docs/router/quality-verifier-architecture.md`](../docs/router/quality-verifier-architecture.md) §3.3, [`../docs/research/code-quality-metrics-assessment.md`](../docs/research/code-quality-metrics-assessment.md) |
| Phase G1 — shadow judge observer (`PendingResponseTextCache`, `JudgeShadowScoreObserver`/`GEvalJudgeClient`/`JudgeModelSelector`/drain worker, `judge_shadow_scores` side table, `is_judge_scored` provenance columns), off by default, judging on a free Providers-screen model | [`../docs/router/geval-shadow-scoring-plan.md`](../docs/router/geval-shadow-scoring-plan.md) |
| Auto-update Phases 0-1 — versioning source of truth, Windows Service hosting — plus update *detection* (`GitHubReleaseCheckClient`, `UpdateCheckHostedService`, `UpdateAdminService` gRPC surface) as originally shipped in Phase 2. Phase 2's *apply* mechanism (a separate `TotallyHotArcRouter.Updater` helper project) is superseded — the GUI now downloads/verifies/launches a single signed MSI installer instead | [`../docs/router/auto-update-plan.md`](../docs/router/auto-update-plan.md) (historical apply design), [`../docs/router/packaging-and-distribution.md`](../docs/router/packaging-and-distribution.md) (current MSI design), [`../docs/router/version-compatibility.md`](../docs/router/version-compatibility.md) (current Router↔GUI versioning) |

**What is still missing**, and which remaining workstream owns it:

- **Measurement is built, has been run, and the ensemble's claimed advantage is not reproduced by it.**
  An earlier revision of this bullet said "nothing computes `R_ij`, the per-task oracle, `CumReg`,
  `AvgPerf`, `TotTok`, or `Perf/$`" — that is no longer true. **N1–N5 shipped:** `RegretReplayResult`
  computes all five metrics against the per-task oracle `a*_i = argmax_j R_ij`, `RegretReplayEngine` runs
  the offline streaming replay with no-leakage enforced at the call boundary, all six comparison baselines
  exist (Always-*m*, DimensionBest, LinUCB, LinTS, kNN Retrieval, LogReg — the last two OOD-only, since
  the ID split publishes no task text), and the Orchestrator arm (`OrchestratorArmFactory`) replays the
  real `OrchestratorRoutingPolicy` — with only `dim_best` and `logreg` wired, since an isolated offline run
  has no live traffic to honestly back `memory_kNN`/`cluster_best`/`llm_router`. **The measured result: the
  exit criterion is not met.** On the real synced corpus, the Orchestrator arm ties `dim_best` bit-for-bit
  on both splits (structurally — `logreg`'s weight cannot outvote `dim_best`'s at their production values)
  rather than beating it, and on OOD a bandit (`linucb`) beats `dim_best`/the Orchestrator on `CumReg`,
  contradicting the paper's expected ordering outright. This is published, not hidden — see
  `regret-evaluation-harness-plan.md`'s N5 status note for the full numbers and why a reduced-voter offline
  harness was always going to struggle to show the ensemble's advantage. **Remaining (N6):** the CLI/GUI
  surface for re-running the harness on demand → **Phase N (N6)**. Closing the *substance* of this gap —
  demonstrating the ensemble actually beats DimensionBest — needs either a live-traffic regret arm or a
  richer offline bootstrap for the three excluded voters; neither is scheduled, and this is now a load-
  bearing fact about the router's actual, measured state, not a formatting gap in an unrun harness.
- ~~**Live traffic is not yet usable as a training corpus.**~~ **Closed by Phases T1–T6.** Transcripts
  are captured opt-in with full provenance (`IsExploratory`, propensity, real cost), skipped embeddings
  are recovered by backfill, the learned cluster taxonomy is trained, voted on (`cluster_best`), and
  measured against the frozen nine-dimension baseline, and the System Settings window exposes the
  adaptive-routing toggle and sample size operators use to turn it on.
- ~~**The Verifier is blind on non-executable dimensions.**~~ **Overtaken by the quality-verifier
  change.** G1 shipped the shadow judge; then code execution was removed from the project entirely, which
  made *every* dimension non-executable and promoted the judge from bystander to co-grader. See the entry
  below.

- **Code execution removed; the Verifier is now static analysis + the G-Eval judge.** The tiered
  sandboxed executor — Linux jail with cgroups v2 and seccomp (Tier 1), Firecracker microVM (Tier 2) —
  was deleted outright, along with its host-capability probe, warm pools, and output redaction. Running
  model-generated code is a risk this project declines to carry under any isolation. What replaced it,
  in `TotallyHotArcRouter.Quality` (renamed from `.Sandbox`):
  - **Static analysis, deepened.** Roslyn for C# and Acornima for JS/TS give authoritative syntax
    verdicts; Python and shell keep a heuristic that is now *explicitly marked* non-authoritative and
    weighted at half. Four composable `IStaticAnalyzer`s add diagnostics, placeholder/stub detection,
    truncation detection, and a complexity band.
  - **The judge promoted.** It now contributes to `u_i` on every graded request rather than writing only
    to `judge_shadow_scores`, and defaults **on** when a free backbone resolves.
  - **One write per request.** `QualityScoreAggregator` joins the two grades by correlation id and
    guarantees exactly one observation reaches `RouterMemory` — enforced by winning a removal race, not a
    flag. Two independent writes would inflate the sample count the voters trust, invisibly.
  - **No execution surface at all.** No subprocess touches model code (no `node --check`, no
    `py_compile`), the assembly has no `Process` reference, and the DI graph is identical on every OS.

  **Documented deviations and honest costs**, per AGENTS.md's deviation rule:
  - *The G2 calibration gate is now unevaluable.* Its first condition — judge rank-correlates with the
    verifier on execution-grounded rows — required ground truth that no longer exists. The judge was
    promoted without the evidence G2 was designed to demand.
  - *The strongest signal is gone.* "It compiled and ran cleanly" outperformed anything static analysis
    can prove. The judge partially compensates; it does not replace it.
  - *Python and shell lost their authoritative check.* A Tier-1 subprocess used to be their real syntax
    verdict; no managed Python parser exists to replace it. IronPython was rejected — it is a full
    interpreter, and referencing it would make "we cannot execute model code" a claim about discipline
    rather than a fact about the assembly.
  - *`is_judge_scored` provenance and the learning-layer policy for judge-influenced rows are still
    outstanding* — G3 required both in the same phase as the promotion, and they did not land. Tracked in
    `geval-shadow-scoring-plan.md` §G3.
  - *Security findings T-11, T-12, and T-18 are closed as no longer applicable*, and the CI step that
    loosened `kernel.apparmor_restrict_unprivileged_userns` for a jail-launch test was removed.
  - *The uncommitted resource-efficiency scoring axis was discarded*; both its inputs (wall-clock, peak
    memory) were execution-derived.

  Full design: [`docs/router/quality-verifier-architecture.md`](../docs/router/quality-verifier-architecture.md).
- **A live corpus cannot be re-keyed after an embedding-model change.** `memory_entries` stores the
  embedding vector but never the prompt text (a deliberate choice — `live-feedback-learning-plan.md`'s
  "Deliberately out of scope" rejects turning router memory into a transcript store), so vectors produced
  by a superseded embedding model cannot be recomputed and are simply filtered out until they age out of
  the FIFO. `request_transcripts.prompt_text` *does* hold the text needed to re-embed them, but only when
  transcript capture is enabled, only within its retention bounds (30 days / 50,000 rows), and no job
  currently does it: `EmbeddingBackfillService` scans `WHERE memory_entry_id IS NULL`, which by
  construction excludes exactly the already-linked rows a re-key would need. Closing this would mean a
  background re-embedding pass over linked transcript rows — the same shape as the existing backfill.
  Unscheduled; recorded so the limitation is tracked rather than rediscovered. Provenance for detecting
  the condition shipped with `live-feedback-learning-plan.md`'s "Embedding-model provenance" section.
- **The general (non-utility) live path has no cost term.** `UtilityRoutingPolicy` prices candidates;
  the Orchestrator does not. T1's real-cost wiring makes the reward computable from live data; putting
  a cost term into the general path's selection is otherwise unscheduled.

```mermaid
flowchart LR
    subgraph shipped["Shipped — the live C-A-F loop, G1, and N1–N5"]
        LOOP["Classifier → 5-voter Orchestrator →<br/>model → Verifier → RouterMemory/EmbeddingMemory →<br/>back into the voters"]
        G1["G1: shadow judge<br/>(accumulates, influences nothing)"]
        N15["N1–N5: metrics core, replay engine,<br/>all 6 baselines, Orchestrator arm<br/>(measured; exit criterion not met)"]
        LOOP -.-> G1
    end

    N["Phase N (N6): CLI/GUI surface"]
    G23["G2 → G3: judge calibration,<br/>then judge as verifier for<br/>non-executable dimensions"]

    LOOP --> N --> G23
    N15 --> N
    G1 -.->|"shadow data gates"| G23
```

## Remaining work, in order

1. **Phase N (N6) — finish the regret evaluation harness's tooling.** N1–N5 shipped (see the shipped-work
   table above), including the Orchestrator arm's real measurement run — whose result was that the exit
   criterion is **not met** as currently scoped (regret-evaluation-harness-plan.md's N5 status note has the
   numbers and why). What remains is only the CLI/GUI surface for re-running the harness on demand; closing
   the *substance* gap (an Orchestrator that actually beats DimensionBest) is unscheduled and needs either
   a live-traffic regret arm or a richer offline bootstrap for `memory_kNN`/`cluster_best`/`llm_router`.
   Detail below; full component spec and per-sub-phase status notes:
   [`../docs/router/regret-evaluation-harness-plan.md`](../docs/router/regret-evaluation-harness-plan.md)
   (N1–N6). Live-feedback Phase 6's remaining item (relocating the TF-IDF `LogRegTrainer` machinery into
   a Phase-N-facing namespace) landed with N4. Phase G1 (shipped; see the shipped-work table above)
   already accumulates shadow judge data passively in the background while this harness is built, so
   G2's gate has volume by the time it's ready.
2. **Phases Q1–Q5 — empirical quality metrics.** Q0 shipped (see the table above). What remains:
   **Q1** generalizes the scorer from two graders to N (`DimensionWeightOptions` keyed grader map,
   per-grader contributions on `QualityResult`, a K-way join, per-grader `DegradedReason`) with the exit
   criterion that a single configured judge produces a byte-identical `UnifiedScore`; **Q2** adds the free
   `IStaticAnalyzer`s (prompt/response relevance, smell density) and makes the judge prompt-aware; **Q3**
   registers the LLM grader portfolio — CodeJudge (correctness), ICE-Score `usefulness`, RACE
   readability/maintainability — each behind its own capability probe that abstains rather than fabricates;
   **Q4** measures per-dimension, per-grader reliability plus verbosity and self-preference skew before any
   re-weighting; **Q5** replaces `DimBestVoter`'s argmax-over-raw-mean with a sample-size-aware estimator,
   accepted only if `RegretReplayEngine` shows `CumReg` improving. Rationale and per-source verdicts:
   [`../docs/research/code-quality-metrics-assessment.md`](../docs/research/code-quality-metrics-assessment.md).
3. **Phases G2 → G3 — judge calibration, then judge-as-verifier.**
   [`../docs/router/geval-shadow-scoring-plan.md`](../docs/router/geval-shadow-scoring-plan.md). G2's
   agreement/calibration analysis runs once G1 has accumulated shadow data; G3 (the judge as scorer of
   record for non-executable dimensions only, with `is_judge_scored` provenance) is gated on G2's
   criteria and never starts if the gate fails.

### Phase N: roadmap-level scope and exit bar

**Prerequisite status:** `live-feedback-learning-plan.md` Phase 4 shipped — every Orchestrator voter
can now cast a real vote on live traffic instead of three of the original four abstaining for lack of an
input, satisfying that plan's own ordering requirement ("measuring voters that structurally cannot fire would
produce a benchmark of `dim_best` wearing an ensemble's name"). The self-organizing-classification-plan's
T phases shipped ahead of N because every phase of them removed a blocker N would otherwise have had to
solve itself, but N never required them to complete.

- ~~Implement the metrics of research-doc §5.1 and A.2~~ — **shipped (N1).** `RegretReplayResult`
  computes the reward matrix `R_ij = ε₁·s_ij + ε₂·κ_ij` (via `RewardWeights`), the per-task oracle
  `a*_i = argmax_j R_ij`, cumulative regret `CumReg_N = Σ(r*_i − r_i(a_i))`, plus `AvgPerf`, `TotTok`,
  `$Total`, and `Perf/$`.
- ~~Offline streaming replay over the restored matrices~~ — **shipped (N1).** `RegretReplayEngine`
  makes no live API calls, matching the handbook's "no API keys required" property, and enforces
  no-leakage at the call boundary rather than trusting each baseline to police itself.
- ~~Implement the comparison baselines as C-A-F configurations (research-doc Table 4)~~ — **all six
  shipped (N1–N4):** Always-*m* (`AlwaysModelBaseline`), DimensionBest (`DimensionBestBaseline`), the
  LinUCB/LinTS contextual bandits (`α = λ = 1`; `v = 0.5, λ = 1`; warm-started on the probing set, seed
  42) over a shared `CategoricalContextBanditBaselineBase`, and kNN Retrieval (`KnnRetrievalBaseline`)/
  LogReg (`LogRegBaseline`) — both need task text, so both are OOD-only, and kNN's index is built and
  queried within OOD leave-one-out rather than the probing split Table 4 names literally (the same
  text-availability constraint LogReg already worked around).
- ~~Wire `OrchestratorRoutingPolicy` in as its own arm and produce the comparison report~~ — **shipped
  (N5)**, via `OrchestratorArmFactory`/`OrchestratorArmBaseline` (only `dim_best`+`logreg` wired — an
  isolated offline run has no live traffic to honestly back the other three voters) and
  `RegretComparisonReportBuilder`. **Remaining (N6):** the CLI/GUI surface for re-running the harness on
  demand.
- **Exit — the real acceptance criterion for this whole plan — measured, not met.** On the real synced
  corpus (2026-08-25), the Orchestrator arm ties `dim_best` bit-for-bit on both ID test and OOD rather than
  beating it on `CumReg`, and on OOD a bandit (`linucb`) beats both, contradicting the paper's expected
  ordering (ArcRouter < DimensionBest < static classifiers < bandits < single models) outright. Absolute
  parity with 205.5 was never expected — the model pool, the verifier, and the embedding model all differ
  — but the *ordering* claim itself is now falsified by this measurement, honestly, as the exit criterion
  demands. Full numbers and the structural reason (voter-weight dominance under a necessarily reduced
  two-voter harness): `regret-evaluation-harness-plan.md`'s N5 status note. Reproducing the paper's claim
  for real is unscheduled follow-up work, not something N5 itself can still deliver by re-tuning.

## Other open work (tracked elsewhere; referenced here so it is not lost)

- [`../docs/router/tracked-todos.md`](../docs/router/tracked-todos.md) — #3 DeepSeek dialect research,
  #4 zero-coverage classes (`TotallyHot.ArcRouter.Quality` now sits at 97.9%; the remaining gap is in
  `TotallyHotArcRouter` at 85.8%), #5 human review of
  tool-call-normalization Phase 5's three design decisions.
- [`../docs/router/tool-call-normalization.md`](../docs/router/tool-call-normalization.md) — Phase 6
  remainder (response/telemetry diagnostics), Phase 7 (native endpoints, design only).
- [`../docs/gui/backlog.md`](../docs/gui/backlog.md) — remaining live-telemetry gaps (Routing ROI /
  Tool Steps / Context Buffer, deliberately mock-backed) and
  [`../docs/gui/governance-model-cards.md`](../docs/gui/governance-model-cards.md)'s missing model
  price channel to the GUI.
- [`../docs/router/agent-resilience-strategies.md`](../docs/router/agent-resilience-strategies.md) —
  Leaky Bucket (pattern 2) not yet built.
- Proposed, unscheduled design docs:
  [`../docs/router/security-hardening-plan.md`](../docs/router/security-hardening-plan.md),
  [`../docs/router/proxy-coexistence.md`](../docs/router/proxy-coexistence.md),
  [`../docs/router/system-proxy-architecture.md`](../docs/router/system-proxy-architecture.md).

## Settled deferrals (do not re-open without new evidence)

- **The quality rescan does not write to router memory** — it grades saved transcript rows and stamps
  the score onto the row only. `IQualityScoreObserver`'s contract is that `QualityScoreAggregator` calls it
  exactly once per request, and `RouterMemory` accumulates a running sum and count, so a second writer
  would double-count every row the live path had already scored — invisibly, since the average still looks
  plausible. Whether rescan scores may reach live memory is deliberately deferred to Phase Q1, which
  reworks that join from one judge to N. Rationale:
  [`../docs/router/quality-verifier-architecture.md`](../docs/router/quality-verifier-architecture.md) §3.3.
- **Multimodal price tiers** — deferred; no upstream feed publishes a `resolution_tier` concept.
  Rationale: [`../docs/router/model-price-catalog.md`](../docs/router/model-price-catalog.md).
- **Routing ROI / Tool Steps / Context Buffer GUI metrics** — deliberately mock-backed; each needs a
  domain concept the codebase does not compute. Rationale:
  [`../docs/gui/backlog.md`](../docs/gui/backlog.md), Cost Analytics bullet.
- **Reasoning-token pricing** — `UsageInfo.ReasoningTokens` exists with no matching price column;
  reasoning tokens bill at the standard output rate. Noted, unscoped.
- **CodeRouterBench `outputs/`, `agentic-artifacts/`, and nested `raw_matrices/`** — not restored;
  nothing in the remaining phases as currently scoped reads them. Rationale: `data/README.md`'s "Not
  yet restored" section.
- **Exact per-cell Table 10 parity for GLM-5/Qwen3-Max/Qwen3.5-Plus/MiniMax-M2.7** — `bug_fixing`,
  `algorithm`, and `test_generation` cells for these four models diverge from the published table by up
  to 0.32 even though row averages (AvgPerf) match within 0.05 for every model; looks like run-to-run
  LLM-as-Judge noise baked into the released CSV, not a parsing bug. Rationale: `data/README.md`'s
  "Known data-fidelity limit" section.
- **`llm_router` substitutes an off-the-shelf model for the paper's unpublished fine-tuned
  checkpoint** — with its sub-deferrals (zero-shot only, no disagreement gating, community-sourced
  artifact URL). Rationale:
  [`../docs/router/orchestrator-ensemble.md`](../docs/router/orchestrator-ensemble.md).
- **Named-model requests are never routed** — a client naming a servable model is naming a command;
  Phase M considered and withdrew superseding `utility-model-routing.md`'s non-goal. Rationale:
  [`../docs/router/orchestrator-live-path-plan.md`](../docs/router/orchestrator-live-path-plan.md) §1.
  (Phase M3.2's editable-toggle deferral is *partially* reopened, deliberately, by
  [`../docs/router/self-organizing-classification-plan.md`](../docs/router/self-organizing-classification-plan.md)
  Phase T6, scoped to exactly two settings.)
- **G1's auto-CoT is a static per-dimension prompt constant, not generated-and-cached** —
  `GEvalJudgeClient.DimensionCriteria` is a hardcoded dictionary rather than a per-dimension prompt
  generated once by a separate LLM call and cached with artifact-version guards; `JudgeOptions.PromptVersion`
  still exists so a future move to generated-and-cached CoT is a version bump, not a schema change.
  **G1's n-sample fallback is a single best-effort numeric parse**, not the G-Eval paper's full n-sample
  estimation, when the judge backbone exposes no logprobs at all. Both are the plan's own allowed "iteration"
  minimum. Rationale: [`../docs/router/geval-shadow-scoring-plan.md`](../docs/router/geval-shadow-scoring-plan.md)
  Phase G1's status blockquote.
- **The judge backbone is a Providers-screen free model, not the hardcoded local endpoint G1 shipped** —
  `JudgeOptions.BaseUrl`/`Model` are removed. `JudgeModelSelector` resolves a route per call from the
  operator's own provider configuration (provider flagged `IsFree`, provider and model enabled, not a
  Bedrock route), and abstains when none is eligible rather than recording a fabricated score. The judge's
  own configuration (`Enabled`, `ModelName`) moved out of `appsettings.json` into `router_settings` behind
  the System Settings window, which also makes `Enabled` a live toggle — including the gate that authorizes
  retaining raw response text in memory. Rationale:
  [`../docs/router/geval-shadow-scoring-plan.md`](../docs/router/geval-shadow-scoring-plan.md) §1a's
  revision note.

---

## Final Validation Gate

Applies at the end of every phase, per [`../AGENTS.md`](../AGENTS.md):

1. `dotnet build` passes with zero warnings and zero errors (`TreatWarningsAsErrors` is on repo-wide).
2. Every new public/protected type and member carries accurate XML documentation; docs on code changed
   by a phase are re-read for staleness, which the compiler cannot check.
3. All unit tests pass; both non-GUI assemblies hold ≥ 80% line coverage per-assembly, as
   `.github/workflows/dotnet-ci.yml` measures it. `TotallyHot.ArcRouter.Quality` sits at ~97.9%, so
   phases touching it must add coverage, not just avoid removing it.
4. No unusually heavy test exceeds 5 seconds. The embedding model load and Phase N's replay harness
   are the live risks here — both belong behind fixtures or environment gates.
5. Every routing decision is logged through Serilog with a **static** message template and structured
   properties. The vote breakdown, the chosen model, and the reward terms are audit-trail data, not
   debug output.
6. Documentation matches delivered behavior — including `README.md` and `docs/HANDBOOK.md`, which
   describe `coderouterbench.db` as synced-on-demand and `outputs/`/`agentic-artifacts/` as not
   restored.
7. Any item deferred during a phase is recorded with its evidence, in the doc that owns the component,
   and summarized in one line under "Settled deferrals" above.
