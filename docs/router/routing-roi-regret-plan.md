# Routing ROI: Expense + Regret vs dim_best, Fast Drain, and Hard Pause Under Load

**Status:** shipped. See `docs/router/self-organizing-classification-plan.md` Phase T4's status block for
the delivered summary.
**Builds on:** [`self-organizing-classification-plan.md`](self-organizing-classification-plan.md) Phase T4 (shipped).

> **Frozen-baseline correction (2026-09-09).** This plan's original "Not changing" list froze
> `DimensionLedger`'s blend rule wholesale to keep the ROI savings figure's finish line from moving as the
> router learns. That froze the wrong thing: the blend rule is *also* `DimBestVoter`'s live production
> picking rule, so freezing it entirely would have blocked ever improving that rule (`src/PLAN.md` Phase
> Q5). Worse, the frozen rule was never actually a fixed yardstick — it prefers the live
> `RouterMemory` average the instant any observation exists, so the "untrained" counterfactual it priced
> was learning right alongside the router it was measured against, holding their gap constant and
> understating (or hiding) any real improvement.
>
> **What changed.** The counterfactual now comes from a new `UntrainedBaselineSelector`
> (`src/TotallyHotArcRouter/Router/UntrainedBaselineSelector.cs`), which reads only the frozen
> CodeRouterBench probing-split prior via `DimensionModelScoreMatrix.SelectBest` - never
> `RouterMemory` - captured at request time (the candidate menu is only known then) into a new
> `request_transcripts.untrained_baseline_model` column, alongside (not replacing) `dim_best_model`.
> `TaxonomyComparisonService.PredictBaselineScore` reads the same frozen matrix directly instead of
> `DimensionLedger`'s blend, dropping the leave-one-out correction for the baseline half entirely (a
> table-only prediction never absorbed the observation being compared, so there is nothing to hold out).
> The four cost ingredients behind `BaselineEstimatedCostUsd` (token averages, catalog prices) are now
> persisted alongside the answer, and `SqliteTaxonomyComparisonStore.UpsertAsync` is first-write-wins
> (`ON CONFLICT DO NOTHING`) so a later rescan can never silently reprice an already-published row against
> drifted inputs. `DimBestVoter`'s own live-preferring blend is untouched - this correction is scoped to
> the ROI yardstick, not the production voter. Zero rows existed in `taxonomy_comparisons` when this
> shipped, so no historical savings figures needed reconciling.
>
> This unblocks Phase Q5's acceptance gate (a sample-size-aware `dim_best` estimator, evaluated by
> `RegretReplayEngine`), which needed exactly this separation to exist - see
> [`regret-evaluation-harness-plan.md`](regret-evaluation-harness-plan.md)'s Q5 status note for why Q5
> itself remains blocked on real traffic even after this fix.
>
> **Frozen-baseline correction, second pass (2026-09-09).** The first pass above still let selection and
> comparison read the prior at two different moments: `UntrainedBaselineSelector.Select` picked the
> baseline model at request time, but `TaxonomyComparisonService.PredictBaselineScore` re-derived its
> score later, from whatever prior `LoadPriorMatrix` had loaded when that comparison cycle ran. An
> explicit CodeRouterBench sync landing in between could swap in a different snapshot, silently pairing
> the request-time model with a comparison-time score - and first-write-wins would then make that
> mismatched pair permanent. Two independent fixes close this: (1) `BenchmarkDatabase.GetContentStamp()`
> now also considers the `-wal` sidecar's mtime (`EnsureCreated` runs this database in WAL mode, so a
> sync's commits can land only in the WAL file and never touch the main file's mtime), and both
> `UntrainedBaselineSelector` and `TaxonomyComparisonService.LoadPriorMatrix` key their cache off it, so a
> sync is observed by both at the same moment; (2) `UntrainedBaselineSelector.SelectWithScore` now returns
> the picked model's score from the exact same prior snapshot it was picked from, and
> `RequestInterceptor` persists it into a new `request_transcripts.untrained_baseline_predicted_score`
> column via `ModelRouteResolutionResult`/`RequestTelemetryPublisher`. `PredictBaselineScore` prefers this
> persisted score over re-deriving one, falling back to `priorMatrix.AverageScore` only for a row written
> before the column existed. The two fixes are complementary, not redundant: (1) narrows the window in
> which selection and comparison could observe different snapshots at all; (2) makes the eventual answer
> correct even within that window, since the model and its score now always travel together from the same
> selection call.
>
> **Shared probing-prior cache (2026-09-10).** `DimBestVoter`, `UntrainedBaselineSelector`, and
> `TaxonomyComparisonService.LoadPriorMatrix` each independently scanned the same frozen "probing" split
> from `BenchmarkDatabase` and kept their own private copy. On a live request, `OrchestratorRoutingPolicy`
> votes through `DimBestVoter` and `RequestInterceptor` separately consults `UntrainedBaselineSelector` for
> the ROI baseline, both within the same request's call stack - so whichever singleton's first request
> happened to arrive first paid a synchronous full-table scan, and the other paid it again independently.
> A new `ProbingPriorMatrixCache` (`src/TotallyHotArcRouter/CodeRouterBench/ProbingPriorMatrixCache.cs`) is
> now injected into all three (DI-registered singleton, with each constructor also accepting an optional
> override defaulting to a private instance so existing direct construction, e.g. tests, is unaffected);
> the scan now runs at most once per corpus sync process-wide. This also incidentally fixes a gap the note
> above about `DimBestVoter`'s blend rule being "untouched" no longer fully describes: `DimBestVoter` used
> to cache its `DimensionLedger` - and the prior snapshot inside it - for the process's entire lifetime,
> silently ignoring every later benchmark sync; it now builds a fresh, cheap-to-construct `DimensionLedger`
> around the shared cache's current matrix on every vote, so an explicit sync reaches its very next vote.
> The blend rule itself (live-memory-preferred, prior as fallback) is unchanged.

## Context

> **Superseded by the frozen-baseline correction above.** This section, the decisions table below, and
> §4's algorithm describe the counterfactual as it stood *before* 2026-09-09: the live `dim_best` judge
> and `DimensionLedger`'s blend. That counterfactual no longer exists in the shipped code - it was
> replaced by `UntrainedBaselineSelector` reading the frozen CodeRouterBench prior directly, with the
> leave-one-out branch dropped entirely (a table-only prediction never absorbed the observation being
> compared). Kept below for the historical record of *why* the drain/pause/caching/regret-formula work
> was originally shaped this way; do not use it as the current counterfactual spec.

The Routing ROI pipeline (Phase T4, `TaxonomyComparisonService`) compares each scored transcript's
actual outcome against the counterfactual "what if the `dim_best` judge alone had picked the model."
Today it records only the **expense** half (`ActualCostUsd` vs `BaselineEstimatedCostUsd` →
`EstimatedNetSavingsUsd`). This plan makes ROI data represent **expense and regret** vs the
dim_best baseline, optimizes the comparison process, and guarantees that ROI computation
**never interferes with servicing incoming requests**.

"Regret" is the project's canonical metric ([technical-reference.md §3.2](../research/technical-reference.md),
[`src/PLAN.md`](../../src/PLAN.md)): per-task reward `r = ε₁·s + ε₂·κ` (score, cost), regret = reward
shortfall vs an alternative policy. The live weights already exist as `RoutingOptions.Epsilon1`/`Epsilon2`
(1.0, −0.1; `src/TotallyHotArcRouter/Models/RoutingOptions.cs`) and are used by `UtilityRoutingPolicy`
— the comparison must use the same weights.

### Decisions confirmed with the operator (2026-08-20)

| Question | Decision |
|---|---|
| Base of the task | Keep the 1-minute cadence + drain-to-completion restructure from the earlier drain plan, but **do NOT erase `dim_best_model`**. Also hunt for optimizations. |
| Regret definition | The docs' reward-based regret: `regret = (ε₁·predictedBaselineScore + ε₂·baselineCost) − (ε₁·observedScore + ε₂·actualCost)`. Positive = dim_best would likely have been better. |
| Taxonomy-accuracy machinery | Kept — `DimensionPredictedScore`/`ClusterPredictedScore`/abs-error fields have no consumer outside the service, store, record, and tests, but they are T4's cluster-vs-dimension evaluation deliverable feeding the future promotion criterion. Their heavy inputs are cached across cycles instead (§4). |
| Regret exposure | **Store only.** New columns on `taxonomy_comparisons` + record fields. `RoutingRoiPoint`, the `/admin/usage/routing-roi` contract, `RoutingRoiPointView`, and the GUI chart are all **unchanged**. |
| Non-interference | **Hard pause:** comparison work runs only while zero proxy requests are in flight; under sustained traffic the backlog simply lags. |

## Changes

### 1. In-flight request gauge (new, small)

New file `src/TotallyHotArcRouter/Proxy/InFlightRequestGauge.cs`: a sealed singleton with an
`Interlocked` counter — `Increment()`, `Decrement()`, `int Count` (volatile read), plus a tiny
`IDisposable Track()` helper so callers can `using var _ = gauge.Track();`.

- `ProxyMiddleware.InvokeAsync` wraps its whole body in `Track()` (try/finally semantics) so
  streaming responses count as in-flight until the last byte. Injected as a new optional
  constructor parameter (`InFlightRequestGauge? inFlightGauge = null`), matching the middleware's
  existing optional-dependency convention; null → no tracking.
- Registered as a singleton in `ServiceCollectionExtensions` and passed to both `ProxyMiddleware`
  and `TaxonomyComparisonService`.

### 2. Queue predicate fix — `SqliteTaxonomyComparisonStore.LoadPendingComparisonsAsync`

Add `AND t.dimension IS NOT NULL` to the WHERE clause. `RunCycleAsync` `continue`s dimensionless
rows without writing a comparison, so with `ORDER BY t.id ASC` such a row sits at the head of every
batch forever — a drain loop would spin on it. A row with no heuristic dimension is not comparable
against the frozen taxonomy, which is precisely what readiness means here. Update
`ITaxonomyComparisonStore.LoadPendingComparisonsAsync`'s doc to name the third readiness condition.

### 3. Schema + record: regret columns

- `TranscriptDatabase`: add `MigrateTaxonomyComparisonRegretColumns` following the existing
  `MigrateDimBestModelColumn` ALTER-TABLE-if-missing pattern, adding two nullable REAL columns to
  `taxonomy_comparisons`: `baseline_predicted_score`, `estimated_regret`.
- `TaxonomyComparisonRecord`: two new fields, `double? BaselinePredictedScore`,
  `double? EstimatedRegret`, with type-level `<param>` docs stating the reward formula, the sign
  convention (positive = the dim_best pick would likely have earned more reward), and that both are
  estimates (the baseline response was never produced).
- `SqliteTaxonomyComparisonStore.UpsertAsync` / `LoadSinceAsync` / `Read`: carry the two columns.

### 4. `TaxonomyComparisonService` — regret, drain, pause, caching

> **Regret in `Compare`/`EstimateCounterfactual` below is pre-correction.** `baselinePredictedScore` is
> now read from the frozen prior matrix (`PredictBaselineScore`), never from `DimensionLedger`, and the
> leave-one-out branch does not apply - see the frozen-baseline correction note at the top of this
> document. The drain/pause/caching/regret-formula mechanics that follow are otherwise still accurate.

**Cadence.** `CheckInterval`: 5 minutes → 1 minute. Still a `BackgroundService` on its own loop.

**Hard pause.** Inject `InFlightRequestGauge` (optional param, null = never pause). In
`RunCycleAsync`: if `gauge.Count > 0` at cycle start, return immediately (log at Debug). Re-check
before each batch fetch and before each row; when traffic arrives mid-drain, stop the drain
(comparison rows already written stay written — the queue naturally resumes next tick). ROI work
only runs on an idle router.

**Drain to completion.** Restructure `RunCycleAsync` (currently one 200-row batch per tick) into an
inner loop:

```
if (!enabled) return
if (inFlight > 0) return
totalCompared = 0
loop:
    pending = LoadPendingComparisonsAsync(batchSize)
    if pending.Count == 0: break
    ensure heavy inputs loaded (first non-empty batch only — see caching below)
    comparedThisBatch = 0
    foreach id in pending:
        cancellationToken.ThrowIfCancellationRequested()
        if (inFlight > 0): log + return          // traffic arrived; resume next tick
        transcript = GetTranscriptAsync(id)
        if no score or no dimension: continue     // row vanished mid-cycle
        record = Compare(...)                     // now includes regret
        UpsertAsync(record); comparedThisBatch++
    if comparedThisBatch == 0: log warning (stuck rows) + break   // termination guard
    totalCompared += comparedThisBatch
log cycle summary when totalCompared > 0
```

**Regret in `Compare`/`EstimateCounterfactual`.** Extend the counterfactual estimate to also return
the baseline predicted score and regret:

- `baselinePredictedScore`:
  - if `Canonicalize(RoutedModel) == Canonicalize(DimBestModel)` →
    `dimensionLedger.PredictLeaveOneOut(liveKey, baseline, observedScore)` (the observation is the
    baseline's own — must be held out, same bias rationale as the existing accuracy comparison);
  - else → `dimensionLedger.Predict(liveKey, baseline)` (plain blend: live average, else prior —
    exactly the number `DimBestVoter` votes on, per `DimensionLedger`'s "measure what the voter
    casts" contract).
- `estimatedRegret = (ε₁·baselinePredictedScore + ε₂·baselineCost) − (ε₁·observedScore + ε₂·actualCost)`
  using `RoutingOptions.Epsilon1/Epsilon2`.
- Null propagation: regret is `null` when any input is missing (no `DimBestModel`, no predicted
  score, unpriceable baseline cost, no actual cost) — never fabricated, matching the existing
  savings behavior. One shot, no retry.
- Extend the per-row `[TAXONOMY-COMPARE]` log line with the estimated regret (static template,
  structured args).

**Caching the heavy inputs.** Today every non-empty cycle re-runs `IMemoryEntryStore.LoadAllAsync`
(all entries + embeddings), `ClusterModelArtifactLoader.TryLoad`, and `ClusterLedger.Build`. Cache
them across cycles in service fields, invalidated by a cheap staleness probe run once per cycle:

- artifact: `File.GetLastWriteTimeUtc(clusterModelPath)` vs cached stamp;
- entries/ledger: a cheap memory-entry count vs cached count.

Rebuild only when either changed. `DimensionLedger` stays per-cycle (it wraps live `RouterMemory`,
which mutates continuously and is cheap to construct). `tokenAverages` reload per cycle (one
aggregate query). Rows compared late in a drain score against the cycle-start snapshot, consistent
with existing per-cycle semantics.

**Batch-size seam for tests.** `ComparisonBatchSize` const → `private readonly int` defaulted to
200, with an `internal` constructor overload taking a batch size (documented test-only, matching
the file's `internal RunCycleAsync` convention), so a drain-across-batches test can use batch
size 2 instead of 201+ seeded rows (5-second unit-test ceiling).

### 5. Tests

`TaxonomyComparisonServiceTests`:

- `RunCycle_RecordsRegretFromLedgerPredictionAndRewardWeights`
- `RunCycle_UnpriceableBaseline_RecordsNullRegretOnce`
- `RunCycle_RoutedEqualsBaseline_UsesLeaveOneOutPrediction`
- `RunCycle_BacklogSpansMultipleBatches_DrainsEveryPendingRow`
- `RunCycle_RowWithNoDimension_IsNeverQueuedAndTheCycleTerminates`
- `RunCycle_RequestsInFlight_DoesNothing`
- `RunCycle_TrafficArrivesMidDrain_StopsAndResumesNextCycle`

Store tests: regret columns round-trip through `UpsertAsync`/`LoadSinceAsync`; the migration adds
the columns to a pre-existing database file.

`ProxyMiddleware` tests: the gauge is incremented during `InvokeAsync` and restored to zero
afterward, including on error paths.

Existing tests re-checked, not rewritten: idempotency, positive-saving, baseline-abstained, and the
MAE-ordering tests (the accuracy machinery is retained).

### 6. Documentation

- [`self-organizing-classification-plan.md`](self-organizing-classification-plan.md) Phase T4
  status block: one-minute full-drain cadence; regret columns added (definition + weights source);
  hard pause on in-flight traffic; `dim_best_model` is retained.
- XML docs on every touched member (compiler-enforced via CS1591 + `TreatWarningsAsErrors`).

## Not changing

`RoutingRoiPointView`, `ManagementFacade.GetRoutingRoiAsync`, the `/admin/usage/routing-roi` JSON
contract, `DimBestVoter`'s own live-preferring blend, and the cluster/dimension accuracy fields.
`request_transcripts.dim_best_model` stays exactly as it was - it feeds the dashboard's
requested-vs-routed telemetry, not the ROI yardstick.

**Superseded by the frozen-baseline correction above:** `RoutingRoiPoint` and the GUI Cost Analytics
chart's *doc comments* now describe the untrained-baseline counterfactual rather than `dim_best`'s live
pick (no field or contract shape changed); `DimensionLedger`'s blend rule was originally frozen wholesale
here to protect the savings yardstick, but that block was replaced by `UntrainedBaselineSelector` reading
the prior directly - the blend rule itself is unchanged and remains `DimBestVoter`'s to evolve (Phase Q5).

## Verification

```bash
dotnet build src/TotallyHotArcRouter.slnx
```

Tests — xUnit v3, run the built exe directly (`dotnet test` reports "Zero tests ran"):

```bash
./src/TotallyHotArcRouter.Tests/bin/Debug/net10.0/TotallyHotArcRouter.Tests.exe
```

Coverage via `dotnet-coverage` (≥80% floor). Live sanity after capturing scored traffic:

```bash
sqlite3 data/transcripts.db "SELECT COUNT(*) AS rows, SUM(estimated_regret IS NOT NULL) AS with_regret, SUM(estimated_net_savings_usd IS NOT NULL) AS with_savings FROM taxonomy_comparisons;"
```

In the logs: `[TAXONOMY-COMPARE]` cycle lines ~1 minute apart when idle; a burst of traffic
produces **no** comparison lines while requests are streaming (hard pause), then a single drain
cycle clears the backlog; per-row lines carry the regret figure.
