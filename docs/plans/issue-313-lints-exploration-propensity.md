# Uncertainty-driven exploration (LinTS) and propensity logging

> Target location: `docs/plans/issue-313-lints-exploration-propensity.md`
> Repo: davidpizon/TotallyHot-ArcRouter (C#/.NET net10.0, no Python, no native binaries, BGE frozen ONNX)
> Status: DRAFT plan only (no PR). Amended 2026-10-19 (free-agent bypass, §4A audit) and 2026-10-20 (David's gap-review decisions: free-agent redefinition, queue-and-retry, epoch-only learning, per-epoch map rebuild, new-agent prior, shared reward, safety net); 2026-10-20 small-question answers (no USD cap, canary parameters, model-agreement gold set, harness-feedback TODO); later 2026-10-20: fixed-choice routes explore post-canary, accuracy-first canary rollback, 3-model gold set.
> All source paths are relative to `src/TotallyHotArcRouter/` unless stated. "(verified)" = checked on `main` on 2026-10-19/20; others UNVERIFIED.

## 1. Current state
- LinUCB / LinTS exist only in the benchmark: `CodeRouterBench/Evaluation/LinUcbBaseline.cs`, `LinThompsonSamplingBaseline.cs`, `CategoricalContextBanditBaselineBase.cs`, driven by `RegretReplayEngine.cs`. Coarse one-hot "dimension" context, not BGE vectors; not in the live path. (Not re-verified.)
- Live exploration: `Router/Orchestrator/OrchestratorRoutingPolicy.cs` (verified) – epsilon-greedy block ~L321; with probability `RoutingOptions.ExplorationRate` (gated by `EnableExploration`) the argmax is replaced by a uniform-random eligible candidate. Defaults `EnableExploration=true`, `ExplorationRate=0.05` in `Models/RoutingOptions.cs` (verified).
- Propensity is logged: `RoutingDecision.IsExploratory` / `Propensity` (`Models/RoutingDecision.cs`, verified), `Router/MemoryEntry.cs`, `memory_entries.is_exploratory` / `propensity` in `Router/RouterMemoryDatabase.cs` (verified).
- Other routing paths log propensity 1.0 (see §4A).

## 2. Design
- Hand-written C#. Math: `System.Numerics.Tensors` + Meta.Numerics 4.2.0 (Cholesky, multivariate normal sampling, CIs).
- Context: BGE embedding projected to 64 dims by an ML.NET PCA map. The map is refit every training epoch (§5) and versioned together with the posterior.
- Policy: 95% greedy (existing argmax unchanged). Inside the 5% slot, sample LinTS (Bayesian linear regression per agent) instead of uniform.
- **Learning happens only in training epochs (David, 2026-10-20).** The live router holds a read-only, versioned posterior snapshot. There are no live/online posterior updates after each graded answer. Each epoch batch-fits every agent's posterior from full history (closed-form Bayesian linear regression; Sherman–Morrison may be used *inside* the batch fit as an implementation detail only).
- Propensity: `p(a) = (1-ε)·1[a = greedy] + ε·π_TS(a)`, `π_TS(a)` from ~1000 posterior draws, floored at `ε·0.01/K` (K = eligible candidates), renormalised.
- Budget: ε = 0.05 hard cap over user (non-internal) traffic. **No dollar cap** (decided 2026-10-20) — the 5% limit is the only exploration budget. Logged propensity uses the ε actually in effect.
- **New agents:** start with an uninformative (wide) prior — zero mean, large prior variance — so LinTS samples them more often, always inside the 5% cap. No borrowing from similar agents, no warm-up hold-out.
- **Fixed-choice routes explore after canary (David, 2026-10-20):** `utility_fixed` (easy tasks) and `light_subagent_fixed` routes stay deterministic until the LinTS snapshot passes its canary (§5A). After that they explore through the *same* `IExplorationStrategy`/LinTS explorer, inside the *same shared* 5% cap (ε counted over all user traffic, not per route), and log real propensities with the formula above (`routing_path` becomes `utility_explore` / `light_subagent_explore` on explored-capable rows). Enabled by `RoutingOptions.FixedRoutesExploreAfterCanary` gated on a canary-passed flag. `legacy_fixed`, `forced`, `fallback` and `substituted` **never** explore.
- **Removed agents:** history kept and used for OPE/direct-method estimates; agent dropped from the candidate set and from the next epoch's posterior.

## 3. Free-agent tasks bypass the bandit (David, 2026-10-19; redefined 2026-10-20)
**Definitions.**
- *Free agent* = an agent with no provider cost, e.g. a locally hosted model. Flag: `ProviderOptions.IsFree` (`Models/ModelRoutingOptions.cs` ~L157, verified) → `ResolvedModelRoute.IsFree` / `RoutingCandidate.IsFree`.
- *Free-agent tasks* = the router's own **internal jobs**: (1) classification (meaning fingerprint / bucketing), (2) LLM judge / grading (#293/#304), (3) training and grader epochs (#292). They never enter the bandit: no LinTS, no 5% slot, no propensity, and they are not counted in the exploration budget.
- Detection is **by internal job type in code**, not by header, model alias or category list. User requests are never free-agent tasks through this mechanism. (`FreeAgentTaskCategories` and RequestInterceptor header detection from the 2026-10-19 draft are **removed**.)

**Where internal jobs call models today (verified on main, 2026-10-20):**
| Internal job | Code | How it reaches a model | Passes through RequestInterceptor / routing policies? |
|---|---|---|---|
| Judge / grading | `Judge/GEvalJudgeClient.cs` (named `HttpClient` via `IHttpClientFactory`, `SendAsync` ~L133–164), `Judge/JudgeGraderClient.cs`, `CodeJudgeGraderClient.cs`, `IceScoreGraderClient.cs`, `RaceGraderClient.cs`, `PortfolioGraderClientBase.cs`; queued by `Judge/GraderQueue.cs` / `GraderDispatcher.cs` / `GraderDrainService.cs` / `GraderScoringJob.cs` | Direct HTTP to the judge model's provider | **No** – bypasses the interceptor and every routing policy |
| Judge model choice | `Judge/JudgeModelSelector.cs` (`EnumerateEligible` ~L147–160) | Picks only `route.IsFree` routes that are provider- and model-enabled; mirrors `RequestInterceptor.GetEligibleRoutes`. Availability in `Judge/JudgeAvailability.cs` | No (own selection) |
| LLM router voter (part of classification/routing) | `Router/Orchestrator/LlmRouterVoter.cs` → `Router/TextGeneration/ITextGenerationClient.cs` → `OnnxTextGenerationClient.cs` | In-process local ONNX small instruct model | No – in-process, no provider, no routing decision row |
| Classification / meaning fingerprint | BGE frozen ONNX embedding (in-process) | No provider model call | No |
| Training / grader epochs (#292) | C# batch jobs over `memory_entries`; model calls only via the judge path above | Judge path | No |

**Consequence:** today the internal jobs already bypass the bandit structurally — none of them goes through `RequestInterceptor` or `OrchestratorRoutingPolicy`. The work is therefore to (a) make this an explicit, enforced invariant (internal jobs use `InternalJobKind` and may only resolve `IsFree` routes), (b) keep internal jobs out of the exploration budget and OPE, and (c) add the queue below. No interceptor-side detection is needed. If an internal job is ever routed through the interceptor in future, it must carry `InternalJobKind` and the orchestrator must short-circuit before the explorer (guard + test).

**No free agent available → queue and retry (never fall back to paid).**
- Durable: **verified 2026-10-20 — `Judge/GraderQueue.cs` is in-memory only** (one unbounded `System.Threading.Channels` lane per grader key, shared `JudgeOptions.QueueCapacity` bound, sheds jobs when full via `DroppedCount`; nothing survives restart). So add a new SQLite table `pending_internal_jobs` (id, job_kind, payload, created_at, attempts, next_attempt_at, last_error, status) via `RouterMemoryDatabase` migration, plus `Router/InternalJobStore.cs`. `GraderQueue` stays as the fast in-memory hand-off; jobs that find no free agent (or are shed when full) are written to `pending_internal_jobs`, and `GraderDrainService` re-hydrates them at startup and on each retry tick.
- Retry with exponential backoff + jitter (e.g. 30 s → 1 h cap), re-checked against `JudgeAvailability` / free-route health before each attempt.
- Max age (default 7 days, configurable); expired jobs are dropped and logged as `internal_job_expired`, the target row stays ungraded (null, never a guessed score).
- Metrics: queue depth, oldest-job age, retries, expirations; alert when no free agent has been available for > N minutes or oldest-job age > threshold.
- Epoch jobs that cannot get a free agent are postponed, not run with partial grades.

**Logging:** internal jobs write no routing-decision rows. Their *outputs* (grades) attach to user rows and still feed quality learning.

## 4. Propensity-logging gaps to close
a. **Other policies logging 1.0** — tag rows with the §4A `routing_path` labels (`utility_fixed`, `light_subagent_fixed`, `legacy_fixed`, `fallback`, `forced`); deterministic rows are excluded from OPE reweighting and the LinTS posterior fit.
b. **Circuit-breaker substitution** — log propensity 1.0 with `routing_path='substituted'`; excluded from OPE. Reward attributes to the agent that actually answered (direct-method only).
c. **Legacy rows** (pre-migration) — propensity NULL, `routing_path='imported'`/legacy, used only in the direct-method part of DR.
d. **Monte Carlo approximation** — floor (`ε·0.01/K`), record `propensity_floor_applied`, clip importance weights in training.

## 4A. Honest propensity for non-orchestrator paths (audit 2026-10-19)
**Principle.** A deterministic policy honestly has propensity 1.0 for its pick and 0 for others. Do not invent confidence percentages. Tag rows with `routing_path` and exclude them from OPE and the posterior. Missing propensity = NULL, never a guessed 1.0.

**`routing_path` values:** `orchestrator`, `utility_fixed`, `light_subagent_fixed`, `legacy_fixed`, `fallback`, `forced`, `substituted`, `imported`. (`free_agent_bypass` dropped: internal jobs write no decision rows, see §3.)

| # | Path | Where | Today | Change | Effort |
|---|---|---|---|---|---|
| 1 | Utility tasks | `Router/CompositeRoutingPolicy.cs` ~105 → `Router/UtilityRoutingPolicy.cs` ~71–151; default 1.0 from `Router/IRoutingPolicy.cs` ~56 | Deterministic argmax, 1.0 | Label `utility_fixed`. Deterministic until LinTS passes canary; then explores via shared LinTS explorer within the 5% cap and logs real propensity | S now; M ~60–100 lines post-canary |
| 2 | Light-subagent near-best | `CompositeRoutingPolicy.cs` ~109–114, `UtilityRoutingPolicy.SelectNearBestValue` ~183–212 | Deterministic, 1.0 | Label `light_subagent_fixed`; deterministic until canary pass, then same LinTS explorer within shared 5% cap | S now; M post-canary |
| 3 | Legacy general policy | `Router/AgentRouterPolicy.cs` ~20–41 via Composite ~119 | Deterministic, 1.0 | Label `legacy_fixed`; retirement candidate | S |
| 4 | Memory-ranking fallback | `Proxy/RequestInterceptor.cs` ~865–890 | 1.0 | Label `fallback`; exclude from OPE | S |
| 5 | User-named / locked model | `RequestInterceptor.cs` ~524, ~615–635 | 1.0 | Label `forced`; exclude from OPE and posterior | S |
| 6 | **BUG** circuit-breaker substitution | `Proxy/RoutingCandidateBuilder.cs` ~159, ~188 | Keeps original propensity but logs substitute model | Propensity 1.0 + `substituted`; exclude from OPE | S–M ~20–40 |
| 7 | Silent 1.0 defaults | `Router/RouterMemoryDatabase.cs` ~335, `Router/MemoryEntry.cs` ~61, `Router/EmbeddingMemory.cs` ~163, `Models/RoutingDecision.cs` ~35, `Proxy/ModelRouteResolutionResult.cs` ~383, `Proxy/BedrockInvocationHandler.cs` ~77 | Missing → 1.0 | Nullable; missing = NULL; in §7 migration | M ~50–80 |
| 8 | Import / backfill | `Sessions/Import/TranscriptImportSink.cs` ~42, `Transcripts/EmbeddingBackfillService.cs` ~175 | 1.0 | NULL, label `imported` | S |

## 5. Use in the #292 training epoch
- **Reward = the live router's quality-minus-cost formula, reused exactly** — `Router/UtilityRoutingPolicy.cs` ~L107–109 and ~L213 (verified): `Epsilon1 · quality + Epsilon2 · cost` with `Epsilon1`/`Epsilon2` from `Models/RoutingOptions.cs` (verified: `Epsilon2` defaults to **−0.1**, documented "negative by convention — a higher κ lowers the reward"; `Epsilon1` = 1.0; both `[Range(-100,100)]`. So cost **does** reduce the score; ranking is `OrderByDescending(reward)`, cold-start s=0 collapses to cheapest-first. Caveat: κ there is the *catalog list price* (blended USD per 1M tokens, `ResolveCost`), not the actual recorded request cost. For LinTS the shared `RewardFunction` must keep the same ε₁/ε₂ but take the **actual recorded cost** per row per the routing rule; add a startup validation that ε₂ ≤ 0). Extract it into a shared `Router/RewardFunction.cs` used by both the utility policy and LinTS so they cannot drift. (Note: `CodeRouterBench/Evaluation/RewardWeights.cs` is benchmark-only; not used.) Unknown cost → row skipped (never 0). Free candidates price at $0 via `ResolveCost` (`IsFree`, ~L224–226).
- **Per-epoch rebuild:** (1) refit the PCA-64 map on current history; (2) reproject all eligible history; (3) batch-fit every current agent's posterior from full history; (4) store map + posterior together as one versioned snapshot (`map_version` = `policy_version`); (5) run OPE and the safety-net gates (§5A); (6) promote or keep incumbent.
- Each decision logs `policy_id`, `policy_version` and `map_version`; propensities never back-filled.
- Promotion estimators: SNIPS + DR; IPS diagnostic. Weight clip 10–20, propensity floor ~0.005, report ESS. Promote only if LCB(candidate) > incumbent (Meta.Numerics intervals).
- Rows excluded from IPS/SNIPS/posterior: unknown cost, `utility_fixed` (pre-canary deterministic rows; post-canary explore-capable rows with real propensity are included), `light_subagent_fixed`, `legacy_fixed`, `fallback`, `forced`, `substituted`, `imported` (last two allowed in direct-method model).

## 5A. Safety net (David, 2026-10-20: in this plan)
LinTS is the first component that changes routing by itself, so it ships with an undo.
- **Canary:** a newly promoted snapshot first serves a small slice of user traffic (**5% of non-forced user requests**, sticky by request hash — decided 2026-10-20); the incumbent serves the rest. Slice and version logged per row.
- **Principle: answer accuracy always outranks speed (David, 2026-10-20).** Latency is never a reason to roll back or reject a canary.
- **Auto-rollback:** mSPRT (always-valid sequential test, safe to check after every answer; built on Meta.Numerics) compares canary vs incumbent continuously. The canary is rolled back automatically, with the event logged/alerted, if **either** (a) graded answer quality (reward per request) is worse at **95% confidence (α = 0.05)**, **or** (b) request failure/error rate rises by **≥ 2 percentage points** over the incumbent (a failed request counts as a wrong answer). **p95 latency is REPORTED ONLY** — never a rollback or rejection criterion. No spend guardrail (no dollar cap). Graduates to 100% only after **at least 200 graded canary answers** with no significant regression. Passing the canary also sets the flag that enables exploration on fixed-choice routes (§2).
- **Judge gold set (no human grading — decided 2026-10-20):** built automatically from real answers graded independently by **3 different free local models** (never paid, never one of the agents being graded) on #293's three criteria — relevance, correctness, completeness — each on a **1–10 scale**. An answer is kept only if, **for each criterion**, all three scores are within **1 point** of each other (max − min ≤ 1); otherwise it is excluded. The consensus (mean) grade is the reference key (SQLite table `judge_gold_set`). **Starting size: 200 answers**, stratified across buckets; refreshed with new agreed items each epoch. **Limitation:** agreement among models cannot catch blind spots they share — documented as weaker than human grades. Every epoch re-grades it; if judge agreement drops below threshold (e.g. rank correlation < 0.60 or MAE > 0.10, matching #304 defaults), the epoch does not promote and raises an alert. Also covers the judge-bias TODO (length correlation).

## 6. Packages
Research Bot (2026-10-19): no maintained pure-.NET contextual-bandit or OPE library. VowpalWabbit 9.11.9 (native, rejected); ExperimentFramework.Bandit 0.23.33 (no license/repo, rejected). Conformal: Lodestar.Conformal 0.2.0 – cross-check only. In-house: bandits ~250–400 LOC, OPE ~150–250, sequential test ~100–150. Full report: `/workspace/arcrouter_dotnet_bandit_ope_conformal.md`.

## 7. Files that change
| File | Change |
|---|---|
| `Router/Orchestrator/OrchestratorRoutingPolicy.cs` | Replace uniform ε block with `IExplorationStrategy`; mixture propensity; log `policy_version`/`map_version`; guard: short-circuit if `InternalJobKind` set |
| `Router/CompositeRoutingPolicy.cs` | Tag utility, light-subagent, legacy rows (§4A) |
| `Proxy/RequestInterceptor.cs` | Tag memory-ranking `fallback` and user-named/locked `forced` (§4A); no free-agent detection |
| `Router/RoutingCandidate.cs` | Reference only (`IsFree`) |
| `Router/InternalJobKind.cs` (new) | Enum: Classification, Judge, Training; carried by internal jobs |
| `Judge/JudgeModelSelector.cs` | Enforce free-only invariant explicitly; expose "no free agent" result for queueing |
| `Judge/GEvalJudgeClient.cs` (+ other `*GraderClient.cs`) | Tag calls with `InternalJobKind.Judge`; never write decision rows |
| `Router/InternalJobStore.cs` (new) | SQLite-backed `pending_internal_jobs` store (GraderQueue is in-memory) |
| `Judge/GraderQueue.cs` / `Judge/GraderDrainService.cs` | Durable queue, backoff+jitter, max age, metrics/alert when no free agent |
| `Judge/JudgeAvailability.cs` | Health signal used by retry |
| `Router/CompositeRoutingPolicy.cs` (fixed-route exploration) | Post-canary: pass utility/light-subagent picks through shared explorer + budget counter; never for legacy/forced/fallback/substituted |
| `Models/RoutingOptions.cs` (fixed-route flag) | `FixedRoutesExploreAfterCanary`, canary-passed gate; canary α, error-rate delta (2 pp), min 200; gold-set models (3), tolerance (1), size (200) |
| `Router/RouterTrainingMetrics.cs` (new, or existing telemetry) | Queue depth, oldest age, retries, expirations; canary/rollback events |
| `Router/RewardFunction.cs` (new) | Shared quality-minus-cost formula extracted from `UtilityRoutingPolicy` |
| `Router/Exploration/IExplorationStrategy.cs` (new) | Sample action + π(a) per candidate |
| `Router/Exploration/UniformExploration.cs` (new) | Current behaviour, default/fallback |
| `Router/Exploration/LinTsExploration.cs` (new) | LinTS sampler over read-only snapshot, MC propensity, floor |
| `Router/Exploration/LinTsPosterior.cs` (new) | Per-agent posterior, batch-fit per epoch, wide prior for new agents, drops removed agents |
| `Router/Training/EmbeddingProjection.cs` (new) | PCA-64 fit per epoch, versioned with posterior |
| `Router/Training/OffPolicyEstimator.cs` (new) | IPS, clipped IPS, SNIPS, DR, ESS, bootstrap CI; exclusion rules |
| `Router/Training/CanaryController.cs` (new) | Canary slice assignment, graduation |
| `Router/Training/SequentialTest.cs` (new) | Always-valid sequential test, auto-rollback trigger |
| `Judge/JudgeGoldSet.cs` (new) | Build model-agreement gold set from 3 free local models, 1–10 scale, keep only if all within 1 point on every criterion; start 200; gate epoch promotion |
| `Models/RoutingOptions.cs` | `ExplorationStrategy`, `PosteriorSamples`, `PropensityFloorFactor`, `NewAgentPriorVariance`, `CanaryFraction` (0.05), `CanaryAlpha` (0.05), `CanaryMinGradedAnswers` (200), `GoldSetAgreeingModels` (2–3), `GoldSetAgreementTolerance`, `InternalJobMaxAge`, retry backoff settings (no `FreeAgentTaskCategories`) |
| `Models/RoutingDecision.cs` | `PolicyId`, `PolicyVersion`, `MapVersion`, `ExploreStrategy`, `PropensityFloorApplied`, `RoutingPath`, `CanaryArm`; `Propensity` nullable |
| `Router/MemoryEntry.cs` | Same fields; nullable propensity |
| `Router/RouterMemoryDatabase.cs` | Migration: `policy_id`, `policy_version`, `map_version`, `explore_strategy`, `propensity_floor_applied`, `routing_path`, `canary_arm`; NULL `propensity`; `pending_internal_jobs` (required — GraderQueue is in-memory); `judge_gold_set` table |
| `Router/SqliteMemoryEntryStore.cs` | Read/write new columns |
| `Router/UtilityRoutingPolicy.cs` | Tag `utility_fixed`/`light_subagent_fixed`; call `RewardFunction`; post-canary route selection through `IExplorationStrategy` within shared 5% cap |
| `Router/IRoutingPolicy.cs` | Remove implicit 1.0 default; require `routing_path`; propensity nullable |
| `Router/AgentRouterPolicy.cs` | Tag `legacy_fixed` |
| `Proxy/RoutingCandidateBuilder.cs` | BUG fix: substitution logs 1.0 + `substituted` |
| `Router/EmbeddingMemory.cs` | Missing propensity → NULL |
| `Proxy/ModelRouteResolutionResult.cs` | Missing propensity → NULL |
| `Proxy/BedrockInvocationHandler.cs` | Missing propensity → NULL |
| `Sessions/Import/TranscriptImportSink.cs` | NULL instead of `?? 1.0`; tag `imported` |
| `Transcripts/EmbeddingBackfillService.cs` | NULL instead of 1.0 |
| `tests/.../LinTsExplorationTests.cs` (new) | Propensities sum to 1, floor, ε cap, wide prior explored more, removed agent never picked, snapshot read-only |
| `tests/.../OffPolicyEstimatorTests.cs` (new) | Estimator correctness; exclusion rules |
| `tests/.../InternalJobBypassTests.cs` (new; replaces FreeAgentBypassTests) | Internal jobs never invoke explorer, write no decision rows, use only `IsFree` routes, not in exploration budget |
| `tests/.../InternalJobQueueTests.cs` (new) | No free agent → queued, never paid; backoff, max-age expiry, survives restart, metrics |
| `tests/.../RewardFunctionTests.cs` (new) | Identical to utility policy's score; unknown cost skipped |
| `tests/.../EpochRebuildTests.cs` (new) | Map refit + full-history rebuild, map/posterior versioned together, `map_version` logged |
| `tests/.../CanaryRollbackTests.cs` (new) | 5% slice assignment, rollback at 95% confidence on synthetic regression, no graduation before 200 graded answers |
| `tests/.../JudgeGoldSetTests.cs` (new) | Gate blocks promotion on judge drift; only items where all free models agree enter the set; paid models rejected |
| `tests/.../SubstitutionPropensityTests.cs` (new) | Substituted rows: 1.0 + `substituted`, excluded from OPE |
| `tests/.../RoutingPathLabelTests.cs` (new) | Every path writes correct label; exclusions; missing propensity NULL |
| `docs/router/orchestrator-live-path-plan.md` | Note M1.2 superseded by this plan |

## 8. Acceptance criteria
- Internal jobs (classification, judge/grading, training) never invoke the explorer, write no routing-decision rows, resolve only `IsFree` routes, and are excluded from the exploration budget; their grades still feed quality learning.
- With no free agent available, internal jobs are queued (durable), retried with backoff, expire after max age with a log/metric, and never fall back to a paid agent.
- Measured exploration rate over user traffic ≤ ε (no dollar cap exists).
- Every decision logs propensity (or NULL), `policy_id`, `policy_version`, `map_version`, strategy and `routing_path` at decision time.
- LinTS posterior changes only at epoch promotion; no live updates.
- Each epoch refits PCA-64 and rebuilds the posterior from full history; map and posterior share one version.
- New agents start with a wide prior and are sampled more within the 5% cap; removed agents are never candidates but their history remains in OPE.
- LinTS reward equals `RewardFunction` = the utility policy's formula; unknown-cost rows skipped.
- Substitutions log 1.0 + `substituted`, never the original propensity; legacy/imported rows NULL and direct-method only.
- No code path or DB default writes a guessed 1.0.
- New snapshots go through canary; mSPRT rolls back automatically when answer quality is worse at 95% confidence or error rate rises ≥ 2 pp; p95 latency is reported but never triggers rollback/rejection; ≥200 graded answers before promotion; promotion blocked when the judge fails the gold set.
- Before canary pass, utility/light-subagent routes never explore; after pass they explore via the LinTS explorer, total exploration across all routes ≤ 5%, with real propensities logged. Legacy/forced/fallback/substituted rows never explore.
- Gold set uses exactly 3 distinct free local models (none paid, none graded), keeps only items with all three within 1 point on each criterion (1–10), initial size 200.
- #292 epoch reports SNIPS, DR, IPS, ESS; promotes only on LCB > incumbent.
- LinTS selection p95 overhead < ~2 ms (reported target only, UNVERIFIED; not a gate).
- No native dependencies; net10.0.

## TODOs (deferred)
- **Harness feedback cues:** investigate whether thumbs up/down (or similar) feedback can be polled or intercepted from the user's harness and used as an extra quality signal for LinTS rewards and canary comparison. Ties to the #304 TODO on harness feedback; not required for this plan.
- Judge-bias TODO (length correlation, pairwise judging) from #304 still applies to the gold set.

## Open questions (unverified code items only)
1. Voter-weighting code location not verified.
2. Full LinTS baseline file set not verified.
3. `CompositeRoutingPolicy` chain role (incl. whether it is the right hook for post-canary fixed-route exploration) not verified.
4. Test project path not verified (`src/TotallyHotArcRouter.Tests/` seen).

Resolved 2026-10-20: free-agent definition (internal jobs); no free agent → queue; PCA-64 refit per epoch; utility/light-subagent explore via shared LinTS explorer after canary pass (legacy/forced/fallback/substituted never); substitution = 1.0 + `substituted`; no USD cap (5% only); canary 5% / 95% / ≥200, mSPRT, rollback on quality or +2 pp errors, latency report-only; gold set = 3 free local models, within 1 point on 1–10 per criterion, 200 to start, no human; GraderQueue is in-memory → new `pending_internal_jobs` table; ε₂ = −0.1 (cost lowers reward).
