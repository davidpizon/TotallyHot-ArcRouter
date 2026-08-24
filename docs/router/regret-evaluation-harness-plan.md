# Regret Evaluation Harness Plan

Implements PLAN.md Phase N: the offline harness that turns every claim the plan makes about the
Orchestrator ("the ensemble beats every single voter," "loop-complete routing beats DimensionBest") from
an assertion into a measurement, replaying CodeRouterBench through the router's actual decision code and
a family of comparison baselines under one reward.

**Status:** in progress — N1, N2, and N3 shipped (see their status notes below); N4-N6 remain. **Ordering:**
after `docs/router/live-feedback-learning-plan.md` Phases 1-4 (shipped) — see that plan's own note that
measuring voters which structurally cannot fire would produce a benchmark of `dim_best` wearing an
ensemble's name. Phases 5-6 of that plan remain open and do not block this one.

## Why this doc exists

Every other phase of this size got a dedicated design doc before implementation
(`coderouterbench-sqlite-migration-plan.md`, `orchestrator-live-path-plan.md`,
`live-feedback-learning-plan.md`). PLAN.md's Phase N section is roadmap-level only: metrics to compute,
baselines to build, one exit criterion. This doc is the component spec, the same relationship
`utility-model-routing.md` has to PLAN.md's Phase describing it.

## Decision: the benchmark corpus is permanent infrastructure, not bootstrap data

**Question this settles.** "Aren't the ID and OOD splits just bootstrap data? Once real requests flow
through the router — including exploratory selections — can't the router derive real-world tables and
replace them?"

**Answer: for two of the three jobs those tables do, yes, and it already happens. For the third, no, and
no volume of live traffic changes that.** The three jobs must be named separately, because conflating
them is what makes the question feel like it has one answer.

| Job the benchmark tables do | What it needs | Replaced by live traffic? | Status |
|---|---|---|---|
| **Prior** — cold-start (dimension, model) quality estimates for `dim_best`; bootstrap rows for `logreg` | Per-model *marginal* averages | **Yes, fully** | **Already implemented** |
| **Stream** — which tasks get replayed for evaluation | A sequence of tasks | **Yes** — real requests are strictly better | Live-arm work |
| **Oracle** — the full outcome matrix `O[i,j]` behind `r*_i = max_j R_ij` | Every model's score on *the same* task | **No — structurally impossible** | Benchmark-only, permanently |

### The prior is already live-replacing (verified in code)

`DimBestVoter.VoteAsync` (`Router/Orchestrator/DimBestVoter.cs`) resolves each candidate as:

```csharp
var live = _routerMemory.GetAverageScore(context.Dimension, candidate.ModelName);
var blended = live ?? _matrix?.AverageScore(priorDimension, candidate.ModelName);
```

Live, execution-grounded feedback wins outright the moment a single observation exists; the probing-set
prior only fills the gap before then. `RouterMemory`'s backing store is an unbounded running aggregate
per (dimension, model) — a `ScoreAggregate` (sum, count) persisted to SQLite's `dimension_scores` table
(see `memory-persistence.md`), whose observation count grows without bound — so this genuinely improves
without limit. `EmbeddingLogRegTrainingService` does the
same for training, blending live memory rows against the OOD bootstrap at
`RoutingOptions.LogRegLiveSampleWeight` (default `3.0`). **Both were designed to age the benchmark out of
the decision path, and both work. No change is proposed here.**

### The oracle cannot be derived from live traffic — exploration adds rows, not columns

The benchmark's outcome matrix has every cell filled, because the corpus authors ran all eight models on
all 9,999 tasks. The oracle is the `max` across a **complete row**.

Live traffic produces rows with exactly **one** filled cell — the model that actually served. Epsilon-
greedy exploration changes *which* cell in a row gets filled; it does not fill a second one. The request
was served once and is gone, so what a different model would have scored on *that same task* is
unobservable, permanently and at any volume.

This is the decisive asymmetry:

- **Marginal statistics** (what does model *m* average on dimension *d*?) accumulate fine from
  single-cell rows, and exploration is exactly what keeps them unbiased. This is why `dim_best` works and
  why the prior is replaceable.
- **Within-task comparison** (on *this* task, which of the eight was best?) requires a filled row. It
  never accumulates, no matter how long the router runs.

`CumReg` is defined on the second. Therefore the benchmark corpus is not a stopgap that graduates into
live data — it is the only source of full rows this system will ever have, and it stays.

### Bound on any live per-task estimate

`RoutingOptions.EmbeddingMemoryCapacity` is `20_000` with FIFO eviction, and `ExplorationRate` is `0.05`.
A full window therefore holds ~1,000 exploratory entries; spread over 8 models and 9 dimensions that is
**~14 randomized samples per (dimension, model) cell**, and it does not grow — the window slides. Any
per-task live estimator is capped there unless the bound is raised or evaluation rows are persisted
outside the FIFO. (The dimension-keyed `RouterMemory` feeding `dim_best` is unbounded and unaffected.)

### Consequence: two metrics that coexist permanently

Not one metric that migrates from benchmark to live, but two answering different questions:

| | **CumReg (exact)** | **Live regret estimate (approximate)** |
|---|---|---|
| Source | Benchmark corpus only | Operator's own traffic |
| Oracle | Measured full rows | Predicted / estimated |
| Grows over time | No — fixed corpus | Yes, to the FIFO ceiling |
| Comparable to | The paper, and every baseline | Only to itself, over time |
| Honest label | A measurement | An estimate with error bars |

The GUI epoch marker belongs to the **right-hand column** — it reports when the operator's own accumulated
data supports an estimate, and separately when all four voters are actually casting votes. It does not,
and cannot, mark a point where live data supersedes the left-hand column.

### The surrogate-oracle option, and its trap

Live data *can* yield a regret-*like* number by predicting the missing cells: `EmbeddingLogRegTrainer`
already trains one regression head per model (*given this embedding, what would model m score?*), so a
full matrix can be synthesized and `max`'d. This is a recognized technique (doubly-robust off-policy
evaluation).

**The trap, recorded before anyone builds it:** `logreg` is simultaneously one of the four voters. Scoring
the router with a component of the router, trained on the router's own history, biases regret downward
whenever the two share an error mode. Any implementation must use a held-out or structurally independent
estimator, and must label its output an estimate — `live-feedback-learning-plan.md`'s "never fabricate
training data" ground rule applies with equal force to fabricated *evaluation* data.

### Prerequisites this implies for any live arm (not yet built)

Recorded here so they are not rediscovered later:

- `RoutingDecision.IsExploratory` exists and its own doc names PLAN.md Phase N as the intended reader, but
  it reaches one Serilog line and is **persisted nowhere** — not on `MemoryEntry`, not on
  `RoutingTelemetryEvent`, not in the `memory_entries` schema.
- The **propensity** (the probability the policy assigned to the arm it chose) is never computed or
  stored. Inverse-propensity estimation requires it.
- `EmbeddingMemoryScoreObserver` writes `cost: 0.0` unconditionally, with a comment stating that a future
  phase needing κ must wire a real cost source first. With κ ≡ 0 every live-derived reward is score-only,
  making the cost-aware half of `r = ε₁·s + ε₂·κ` unmeasurable from live data.

None of these block the offline harness (N1-N5), which needs none of them.

## The constraint that shapes everything below

`live-feedback-learning-plan.md` already established, and verified against the synced corpus, that
**CodeRouterBench publishes no task text for the ID splits.** `benchmark_id_tasks` carries only
`task_id`/`split`/`source_split`/`dimension` — no `prompt`. Only `benchmark_ood_tasks` (176 rows) has
text.

PLAN.md's Phase N exit criterion targets **the restored ID test split** specifically (2,919 tasks). That
creates a real tension this doc has to resolve rather than paper over:

| Component | Needs task text/embedding? | Runs on ID test? |
|---|---|---|
| `dim_best` voter / DimensionBest baseline | No — dimension string only | Yes |
| Always-*m* baseline | No — just per-model scores | Yes |
| LinUCB / LinTS (categorical context variant) | No — dimension/difficulty/language one-hot | Yes |
| `memory_kNN` voter / kNN Retrieval baseline | Yes | **No** |
| `logreg` voter / LogReg baseline | Yes | **No** |
| `llm_router` voter | Yes (task text) | **No** |
| LinUCB / LinTS (embedding-projection context variant) | Yes | **No** |

Research-doc §A.1 itself offers the way through: LinUCB/LinTS's context is documented as *either*
"categorical (dimension/difficulty/language one-hot) **or** 64-dim JL projection of task embedding" — the
categorical variant was always meant to be the text-free option. This harness implements **only the
categorical-context variant** of the bandits; the embedding-projection variant is out of scope for the
same reason `memory_kNN`/`logreg`/`llm_router` are, and is recorded as a deferral, not silently dropped.

**Consequence for the "Orchestrator" arm of the exit criterion.** On the ID test split, replaying
`OrchestratorRoutingPolicy` reduces to `dim_best` casting the only non-abstaining vote — `memory_kNN`,
`logreg`, and `llm_router` all abstain for lack of text, exactly as they do today for CodeRouterBench
tasks generally. This is not a harness bug; it is the corpus's own published limitation, already load-
bearing elsewhere in the plan. The exit criterion's "ordering, not absolute parity" framing already
anticipates this kind of gap ("the model pool, the verifier, and the embedding model all differ"). This
doc adds the harness itself as a documented reason the *first* published number may differ from the
paper's, and requires the ID-test result to say in its own output which voters fired.

**The OOD split is offline-runnable in full**, because it has both text (for embeddings) and full
feedback (all 8 models scored on all 176 tasks). The harness supports it as a second, complete replay
target from the start — not because PLAN.md requires it for the exit criterion, but because it is the
only split where every voter and every baseline (including embedding-context bandits and the live
`logreg` artifact) can actually be exercised, and reporting "ID: dim_best-only, OOD: full ensemble" is
more honest than reporting only the split that doesn't demonstrate the ensemble.

## Metrics (research-doc §3.2, §5.1, A.2)

Given the outcome matrix `O[i,j] = (s_ij, κ_ij)` — `s_ij` = verifier score in `[0,1]`, `κ_ij` = `cost_usd`
— both already columns of `benchmark_id_results`/`benchmark_ood_results`:

- **Reward matrix:** `R_ij = ε1·s_ij + ε2·κ_ij`, canonical weights `(ε1, ε2) = (1, -0.1)` (§A.2), i.e.
  `r_i(a_i) = s_i(a_i) - 0.1·κ_i(a_i)`. Configurable, defaulting to canonical.
- **Per-task oracle:** `a*_i = argmax_j R_ij`, `r*_i = max_j R_ij` — full prior knowledge, computed once
  per task from the outcome matrix, independent of any router under test.
- **CumReg_N(π) = Σ_i (r*_i − r_i(a_i))** — sum of per-task regret against the *per-task* oracle. The doc
  is explicit this is **not** the gap to a single best-arm policy; do not implement it as one.
- **AvgPerf** = mean `s_i(a_i)` over the replayed stream (not reward — score only).
- **TotTok** = sum of `total_tokens` (or `input_tokens + output_tokens` where `total_tokens` is null)
  over the replayed stream, for the model actually selected each task.
- **$Total** = sum of `cost_usd` over the replayed stream, for the model actually selected. Falls back to
  `benchmark_models.input_per_1m`/`output_per_1m` × token counts when a result row's own `cost_usd` is
  null, matching the pricing columns Phase 1 already fixed.
- **Perf/$** = `AvgPerf% / $Total`.

All five are computed by one `RegretReplayResult` accumulator so every router (baseline or the real
Orchestrator) reports directly comparable numbers from the same replay loop — never a bespoke calculation
per baseline.

## Replay engine

**Offline, streaming, no live API calls** (handbook's "no API keys required" property, and matches
`CodeRouterBenchTable10ReconciliationTests`'s existing network/file-gated convention: self-skips when the
corpus isn't synced).

- `RegretReplayEngine` iterates a split's tasks in a fixed, seeded order (deterministic — same convention
  as the dataset's own MD5 seed `coding-router-v1`), builds the per-task outcome row from
  `benchmark_id_results`/`benchmark_ood_results`, and for each task: asks the router under test (an
  `IRegretBaselineRouter` or the real `OrchestratorRoutingPolicy`) to pick a model given only the context
  that router is allowed to see, looks up `R_i,a_i` from the outcome row, and accumulates.
- **No leakage.** A baseline only ever receives what its Table 4 row says it may see (dimension for
  DimensionBest, task embedding for kNN/LogReg, categorical context for bandits) — never the task's own
  outcome row before it commits to a model. This is the harness's core correctness property and gets its
  own test: a baseline that peeks at `R_ij` before choosing is a bug, not an optimization.
- Warm-start where Table 4 specifies one (DimensionBest's frozen probing-set prior; LinUCB/LinTS "warm-
  started on probing set, seed 42") reads from `split = 'probing'` before the streaming loop begins over
  `split = 'id_test'` or the OOD table; the warm-start data itself is never part of the scored stream.

## Baselines (research-doc Table 4, feasible subset)

Each is a small stateless-or-online policy implementing a shared `IRegretBaselineRouter` (`Route(context)
-> modelId`), independent of the production `IRoutingPolicy` interface — these are evaluation-only
constructs, not routing policies the live proxy could register.

- **Always-*m*** — one instance per candidate model, ignores context entirely. Reference floor; also the
  natural sanity check (`CumReg` for Always-Opus should roughly match Opus's own row average gap).
- **DimensionBest** — looks up `DimensionModelScoreMatrix.FromDatabase(db, "probing")` (already exists,
  `CodeRouterBench/DimensionModelScoreMatrix.cs`), argmax over candidates for the task's dimension. This
  is the *frozen* baseline — deliberately not `DimBestVoter`'s live-memory-preferring version, since Table
  4 specifies "frozen probing-set prior" for the static family.
- **kNN Retrieval** — nearest-neighbor over a frozen probing-set embedding index (OOD only, per the
  constraint above; the harness reports it absent on ID test rather than silently substituting something
  else).
- **LogReg** — reuses the existing `CodeRouterBench/LogRegTrainer.cs` TF-IDF artifact (already trained
  from the OOD split, per `orchestrator-ensemble.md`'s `logreg` history and
  `live-feedback-learning-plan.md` Phase 6's relocation note). OOD only, for
  the same reason.
- **LinUCB / LinTS (categorical-context variant)** — `α = λ = 1` (LinUCB), `v = 0.5, λ = 1` (LinTS),
  one-hot context over dimension (× difficulty × language where the classifier exposes them), warm-started
  on the probing split with `seed = 42`, online per-arm posterior update using the same canonical reward
  as every other arm (§A.2's explicit requirement — "every router is scored under the same canonical
  evaluation reward"). Runs on both ID test and OOD.

**Deferred, not attempted here** (recorded per the repository's standard rather than silently dropped):

- TF-IDF+MLP, RouteLLM-MF, RouteLLM-BERT, Qwen3.5-FT — none has an existing implementation in this
  codebase to reuse, each is a materially separate modeling project, and PLAN.md's exit criterion does not
  name them. Out of scope for this phase entirely.
- LinUCB/LinTS embedding-projection context variant — same text/embedding availability gap as kNN/LogReg
  on the ID split.

## The Orchestrator arm

Replays the real `OrchestratorRoutingPolicy` (not a re-implementation) against `VotingContext`s built from
each task's available signals — dimension always, embedding/text only where the split provides them (OOD)
— through the same replay loop, so its `CumReg`/`AvgPerf`/`Perf/$` are computed identically to every
baseline's. Exploration (`RoutingOptions.EnableExploration`) is disabled for the harness run — regret
evaluation measures the policy's exploitation quality; a separate, explicitly-labeled exploratory run can
report the cost of exploration itself later, but is not part of this phase's exit criterion.

## Namespace and layout

Lands in a new `TotallyHot.ArcRouter.CodeRouterBench.Evaluation` namespace under
`src/TotallyHotArcRouter/CodeRouterBench/Evaluation/`, and — per Phase 6 of
`live-feedback-learning-plan.md`, explicitly deferred to "land alongside Phase N itself" — `LogRegTrainer`,
`LogRegTextTokenizer`, `LogRegModelArtifact`, and `LogRegModelArtifactSerializer` relocate into it at the
same time, since they become this phase's LogReg-baseline implementation rather than router-voter
infrastructure. `DimensionModelScoreMatrix` stays where it is (`CodeRouterBench/`) — it is still `dim_best`
voter infrastructure too, not evaluation-only.

## Sub-phases

Landed incrementally; each is independently testable and mergeable.

- **N1 — Metrics core + replay engine + Always-*m*. Shipped.** `RegretReplayResult`, `RegretReplayEngine`,
  `IRegretBaselineRouter`, `AlwaysModelBaseline`, reward-matrix/oracle computation, all under
  `src/TotallyHotArcRouter/CodeRouterBench/Evaluation/`. `RegretReplayContext` carries only
  dimension + per-task candidate ids (derived from that task's own outcome cells, not a fixed global
  roster — the candidate-set decision this doc left open) so a baseline's `Route` can never see the
  outcome row before committing, enforced at the `RegretReplayEngine` call boundary rather than trusted
  per-implementation. `Route` is synchronous (`string? Route(RegretReplayContext)`) since N1/N2's
  baselines do no I/O; a baseline returns `null` for a task it cannot route (its target model was never
  scored on it), and that task is excluded from its metrics rather than counted as a zero. Exit
  criterion met: `RegretReplayEngineTests.Replay_AlwaysOpus_MatchesHandComputedMetrics` replays
  Always-Opus over a fixture outcome matrix and asserts hand-computed `CumReg`/`AvgPerf`/`TotTok`/`$Total`/`Perf/$`.
- **N2 — DimensionBest baseline. Shipped.** `DimensionBestBaseline` wraps the existing
  `DimensionModelScoreMatrix.FromDatabase` (frozen probing-split prior, deliberately not
  `DimBestVoter`'s live-memory-preferring version), ties broken by ordinal model id to match
  `OrchestratorRoutingPolicy`'s own tie-break. Exit criterion met:
  `DimensionBestBaselineTests.Replay_FrozenPriorDisagreesWithTruePerTaskWinner_RegretReflectsIt` uses a
  fixture where the frozen prior and the true per-task winner disagree and asserts nonzero `CumReg`.
- **N3 — Bandits (categorical context, ID + OOD). Shipped.** `LinUcbBaseline` (`α = λ = 1`) and
  `LinThompsonSamplingBaseline` (`v = 0.5, λ = 1`, seeded `Random`, default seed `42`), both built on a new
  shared `CategoricalContextBanditBaselineBase`. Context is one-hot over `RegretReplayContext.Dimension` —
  the only signal the ID split's baselines have — which makes the general LinUCB/LinTS ridge matrix `A = λI
  + Σx·xᵀ` diagonal by construction, so each (arm, dimension) pair reduces to a scalar pull-count/reward-sum
  pair with no matrix inverse needed. Online updates required extending the harness itself: a new
  `IOnlineRegretBaselineRouter.Update(context, selectedModelId, reward)` interface, called by
  `RegretReplayEngine.Replay` immediately after `Route` commits and only with the *selected* model's own
  reward — preserving N1's no-leakage property for the update path, not just the selection path.
  `WarmStart(probingTasks, weights)` runs the bandit's own `Route`/`Update` loop over the probing split
  before the scored stream, so "warm-started on the probing set" reuses the same online mechanics rather
  than a separate code path. Exit criterion met:
  `LinUcbBaselineTests.Replay_OneArmStrictlyBetter_ConvergesToPickingIt` and the LinTS equivalent both
  assert `AvgPerf` converges toward the strictly-better arm's score over 200 tasks;
  `LinThompsonSamplingBaselineTests.Replay_SameSeed_IsDeterministicAcrossRuns` and
  `WarmStart_SeededTwice_ProducesIdenticalPostWarmStartRoute` assert seed-42 reproducibility bit-for-bit.
- **N4 — kNN Retrieval + LogReg baselines (OOD only).** Reuses/relocates `LogRegTrainer`. Exit: both report
  "not computable — no task text" when pointed at `id_test`, and produce a real, non-placeholder score on
  OOD from the synced corpus.
- **N5 — Orchestrator arm + full comparison report.** Wires `OrchestratorRoutingPolicy` into the replay
  loop, produces the ID-test and OOD comparison tables (all baselines + the Orchestrator, same columns as
  research-doc Table 3), and evaluates PLAN.md's exit criterion: does the observed `CumReg` ordering match
  the paper's (Orchestrator < DimensionBest < static classifiers < bandits < single models), and does the
  Orchestrator beat DimensionBest? Publishes the numbers obtained either way — the plan's exit criterion is
  explicit that failing to reproduce the ordering must be reported honestly, not hidden or re-tuned until
  it passes.
- **N6 — CLI/GUI surface for re-running the harness on demand.** Follows the `--sync-benchmark-data` /
  Governance-pane pattern once N1-N5 are proven; not required for the exit criterion itself.

## Exit (whole phase, echoing PLAN.md)

1. `dotnet build` zero warnings/errors; every new public/protected member has accurate XML docs.
2. All unit tests pass, fixture-based (no real corpus access, no real ONNX inference — matching Phase 4's
   "no test exceeds 5 seconds" bar); a separate, self-skipping reconciliation test (matching
   `CodeRouterBenchTable10ReconciliationTests`'s convention) runs the real N5 report against a synced
   corpus when one is present.
3. Every baseline and the Orchestrator arm report `AvgPerf`/`CumReg`/`TotTok`/`$Total`/`Perf/$` from the
   one shared accumulator; the ID-test report states explicitly which voters/baselines were text-limited.
4. The N5 report is published (in this doc's changelog or a linked results file) with the observed
   ordering and the Orchestrator-vs-DimensionBest comparison, whichever way it comes out.
5. Deferred items (TF-IDF+MLP, RouteLLM variants, Qwen3.5-FT, embedding-context bandits) are recorded here,
   not reopened without new evidence, per PLAN.md's "Settled deferrals" convention.
