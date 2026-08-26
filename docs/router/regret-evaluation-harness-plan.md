# Regret Evaluation Harness Plan

Implements PLAN.md Phase N: the offline harness that turns every claim the plan makes about the
Orchestrator ("the ensemble beats every single voter," "loop-complete routing beats DimensionBest") from
an assertion into a measurement, replaying CodeRouterBench through the router's actual decision code and
a family of comparison baselines under one reward.

**Status:** in progress — N1-N5 shipped (see their status notes below); N6 remains. N5's own exit
criterion (Orchestrator beats DimensionBest, and reproduces the paper's regret ordering) was measured and
**not met** as this harness is currently scoped — see N5's status note for the real numbers and why.
**Ordering:**
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
- **kNN Retrieval** (`KnnRetrievalBaseline`, N4, shipped) — majority vote over the *k* nearest neighbors
  (embedding cosine similarity, i.e. dot product since `EmbeddingResult.Vector` is already
  unit-normalized) in a frozen `KnnRetrievalArtifact` index built by `KnnRetrievalIndexBuilder`. OOD only,
  per the constraint above; on ID test it reports absent (`Route` returns `null` for every task) rather
  than silently substituting something else. **Deviation from Table 4's literal "frozen probing-set
  embedding index":** the probing split publishes no task text, the same constraint that forced LogReg
  onto the 176-task OOD split — this baseline's index is therefore built *and* queried entirely within
  OOD, leave-one-out (a query task's own entry is excluded from its neighbor search), an honest
  reconstruction rather than an exact reproduction of Table 4. No live embedding calls happen during
  replay: every OOD task's embedding is precomputed once by `KnnRetrievalIndexBuilder` (an offline step
  that does call a real `IEmbeddingClient`), and `Route` only ever looks up a query's own precomputed
  entry by task id — never embeds arbitrary text — preserving the harness's "no live API calls" property.
- **LogReg** (`LogRegBaseline`, N4, shipped) — real TF-IDF inference (argmax over one-vs-rest class
  scores) against the existing `LogRegTrainer`-trained artifact (already trained from the OOD split, per
  `orchestrator-ensemble.md`'s `logreg` history and `live-feedback-learning-plan.md` Phase 6's relocation
  note). OOD only, for the same reason; `Route` returns `null` whenever `RegretReplayContext.TaskText` is
  unpublished (every ID-test/probing task).
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

**Shipped (N5) with a deliberately reduced voter set.** `OrchestratorArmFactory.Build` wires the real
`OrchestratorRoutingPolicy` (`Router/Orchestrator/OrchestratorArmFactory.cs`) with only two of the live
ensemble's five voters:

- **`dim_best`** — a real `DimBestVoter` over the frozen probing-split prior, backed by a fresh, empty
  `RouterMemory` so it never touches (or is influenced by) an operator's live memory database; it degrades
  to the frozen prior on every task, exactly like `DimensionBestBaseline`.
- **`logreg`** — a real, embedding-based `LogRegVoter`, trained fresh and in-memory (via the production
  `EmbeddingLogRegTrainer`, never touching an operator's own `logreg_voter_model.json`) from the same OOD
  outcome rows and precomputed embeddings N4 already loads for `KnnRetrievalBaseline` — no second embedding
  pass, no live API call anywhere in the harness's replay path.

**`memory_kNN`, `cluster_best`, and `llm_router` are deliberately excluded.** `memory_kNN` and
`cluster_best` would otherwise need "memory"/cluster state manufactured from the same 176-task evaluation
corpus being scored — a real judgment call, resolved by leaving them out rather than fabricating a live
history that never happened. `cluster_best` doubly so: fitting a taxonomy to 176 tasks split across ~9
dimensions would leave nearly every cluster below `RoutingOptions.ClusterBestMinObservations` and abstain
everywhere anyway. `llm_router` requires a real local-model generation call, which the harness's "no live
API calls" property forbids outright. This is a documented limitation of an isolated, offline harness with
no live traffic behind it (this repository's own dev/build machine has no `agent_telemetry.db`,
`transcripts.db`, or trained voter artifacts at all) — not a claim that the live 5-voter ensemble behaves
identically.

**Observed consequence, reported honestly per this plan's own standard.** With only `dim_best` (weight
`0.9`) and `logreg` (weight `0.43`) wired, the Orchestrator arm's weighted argmax can never flip away from
`dim_best`'s pick unless `dim_best` abstains — `0.43` cannot outweigh `0.9 × confidence` for any
`dim_best`-confidence above `≈0.48`, which every task in this corpus clears. Empirically, on the real synced
corpus, `orchestrator`'s `CumReg`/`AvgPerf`/`TotTok`/`$Total` are **bit-for-bit identical to `dim_best`'s**
on both ID test and OOD (N5's reconciliation run, below) — `logreg` never overturns a single decision.
This is a real, structural property of the two-voter reduction, not a measurement bug, and it is the reason
N5's exit criterion (see below) is not met by this harness as scoped.

## Namespace and layout

Lands in a new `TotallyHot.ArcRouter.CodeRouterBench.Evaluation` namespace under
`src/TotallyHotArcRouter/CodeRouterBench/Evaluation/`, and — per Phase 6 of
`live-feedback-learning-plan.md`, explicitly deferred to "land alongside Phase N itself" — `LogRegTrainer`,
`LogRegTextTokenizer`, `LogRegModelArtifact`, and `LogRegModelArtifactSerializer` relocated into it at N4,
since they became this phase's LogReg-baseline implementation rather than router-voter infrastructure
(every prior in-namespace reference — `ClusterTermExtractor`'s shared tokenizer, `OodBootstrapSampleSource`/
`OodClusterBootstrapSampleSource`'s prompt extraction, `LogRegVoter`'s doc comments — now points at the new
namespace). `DimensionModelScoreMatrix` stays where it was (`CodeRouterBench/`) — it is still `dim_best`
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
- **N4 — kNN Retrieval + LogReg baselines (OOD only). Shipped.** `LogRegTrainer`/`LogRegTextTokenizer`/
  `LogRegModelArtifact`/`LogRegModelArtifactSerializer` relocated into
  `TotallyHot.ArcRouter.CodeRouterBench.Evaluation` (see "Namespace and layout" above); `LogRegTrainer`
  gained `LoadOodTrainingExamples`/`BuildVocabularyIndex`/`ComputeTfIdf` as internal (not private) members
  so `LogRegBaseline` and `KnnRetrievalIndexBuilder` reuse its exact loading, labeling, and featurization
  logic rather than duplicating it. `RegretTaskOutcome`/`RegretReplayContext` gained an optional
  `TaskText` field (`null` on every split but OOD), threaded through by `RegretReplayEngine` alongside
  dimension and candidate ids — carrying no additional leakage, since it is never derived from
  `RegretTaskOutcome.Cells`. `OodRegretTaskOutcomeLoader` builds real `RegretTaskOutcome` rows (with text)
  from the synced OOD split, resolving cost from the row's own `cost_usd` or, when absent, from
  `benchmark_models` pricing over its token counts — the shared loader N5 reuses for its own OOD arm.
  `LogRegBaseline` does real TF-IDF inference from `RegretReplayContext.TaskText`; `KnnRetrievalBaseline`
  looks up a query task's own precomputed embedding by id in a `KnnRetrievalArtifact` frozen index (built
  offline by `KnnRetrievalIndexBuilder`, the only call site that ever touches an `IEmbeddingClient`) and
  majority-votes its *k* nearest (leave-one-out) neighbors' labels — see "Baselines" above for the kNN
  deviation from Table 4's literal "probing-set" wording. Exit criterion met:
  `N4BaselinesReconciliationTests.Replay_OnRealOodCorpus_ProducesNonPlaceholderScoresForBothBaselines`
  (self-skipping like `LogRegTrainerReconciliationTests`) replays both baselines over the real synced OOD
  corpus and asserts a nonzero scored-task count and a finite `CumReg` for each; the ID-test "not
  computable" half needs no real corpus and is covered by
  `LogRegBaselineTests.Route_TaskTextAbsent_ReturnsNull` and
  `KnnRetrievalBaselineTests.Route_TaskIdNotInFrozenIndex_ReturnsNull`.
- **N5 — Orchestrator arm + full comparison report. Shipped.** `OrchestratorArmFactory`/`OrchestratorArmBaseline`
  wire the real `OrchestratorRoutingPolicy` into the replay loop (see "The Orchestrator arm" above for the
  reduced two-voter wiring and why). `IdSplitRegretTaskOutcomeLoader` builds real `RegretTaskOutcome` rows
  from `benchmark_id_results` for either the `probing` or `id_test` split (continuous `score` column, no
  resolved-to-score conversion, `TaskText` always `null`), and `BenchmarkModelPricingLookup` factors the
  cost-fallback logic N4's OOD loader already had so both loaders resolve cost identically.
  `RegretComparisonReportBuilder.BuildReport` replays every Always-*m* (one per model observed in the
  split), `DimensionBestBaseline`, both bandits (warm-started on `probing`), `LogRegBaseline`,
  `KnnRetrievalBaseline`, and the Orchestrator arm over one split through the shared accumulator, and
  `FormatMarkdownTable` renders research-doc Table 3's columns plus scored/skipped counts — the "which
  baselines were text-limited" signal exit criterion #3 requires, shown as a `0`/full-skip row rather than
  omitted. Exit criterion met (build + report produced):
  `N5ComparisonReportReconciliationTests.Replay_OnRealCorpus_ProducesTheFullComparisonReport`
  (self-skipping like `N4BaselinesReconciliationTests`) builds and prints both splits' reports against the
  real synced corpus and asserts every row's `CumReg` is finite.

  **The real acceptance criterion result, published per this doc's own "either way" mandate — not met.**
  Running the reconciliation test against this repository's synced corpus on 2026-08-25 (dev/build machine,
  no live traffic; see "The Orchestrator arm" above) produced:

  | Split | dim_best `CumReg` | orchestrator `CumReg` | Orchestrator beats dim_best? | Best-of-all-computable `CumReg` |
  |---|---:|---:|---|---|
  | ID test (2,919 tasks) | 244.3459 | 244.3459 (bit-identical) | **No — exact tie** | dim_best/orchestrator (tied lowest) |
  | OOD (176 tasks) | 43.7286 | 43.7286 (bit-identical) | **No — exact tie** | `linucb` at 41.4336 (lower than dim_best/orchestrator) |

  Neither half of PLAN.md's exit criterion holds under this harness as scoped: the Orchestrator arm does
  not beat DimensionBest (it structurally cannot, with only `dim_best`+`logreg` wired at their production
  weights — see "The Orchestrator arm"'s consequence note), and on OOD the paper's expected ordering
  (Orchestrator < DimensionBest < static classifiers < **bandits** < single models) is contradicted outright
  — `linucb`, a bandit, achieves *lower* regret than both DimensionBest and the Orchestrator arm, the
  opposite of where the paper's ordering places bandits. `logreg`/`knn_retrieval` correctly report
  "not computable" (zero scored, all skipped) on ID test; on OOD, `knn_retrieval` skips 37 of 176 tasks
  (a nearest neighbor whose only viable neighbors' labels fell outside that task's candidate pool) and
  `logreg` (the standalone TF-IDF baseline) always picks `qwen3-max`, the model its "cheapest resolver
  wins" training label favored most often in-sample. Full tables (all 8 Always-*m* baselines, both bandits,
  every column) are reproduced by re-running the reconciliation test above; this changelog entry is the
  "publish the numbers obtained either way" artifact, not a summary that omits the unfavorable half.

  **Read this honestly, not as "the ensemble doesn't work."** This result is a property of the *harness's*
  necessarily reduced two-voter reconstruction (an isolated, one-shot offline run has no live traffic to
  honestly back `memory_kNN`/`cluster_best`, and no local LLM to back `llm_router`), not a measurement of
  the live, full five-voter, sample-size-aware production ensemble. It is exactly the kind of finding
  PLAN.md's exit criterion exists to surface rather than hide: the current offline reconstruction cannot
  yet demonstrate the ensemble's claimed advantage, and closing that gap (a live-traffic arm, or a richer
  offline bootstrap for the other three voters) is now a load-bearing fact about what remains, not an
  assumption.
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
