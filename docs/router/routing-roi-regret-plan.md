# Routing ROI: Expense + Regret vs dim_best, Fast Drain, and Hard Pause Under Load

**Status:** shipped. See `docs/router/self-organizing-classification-plan.md` Phase T4's status block for
the delivered summary.
**Builds on:** [`self-organizing-classification-plan.md`](self-organizing-classification-plan.md) Phase T4 (shipped).

## Context

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

`RoutingRoiPoint`, `RoutingRoiPointView`, `ManagementFacade.GetRoutingRoiAsync`, the
`/admin/usage/routing-roi` JSON contract, the GUI Cost Analytics chart, the `request_transcripts`
schema (`dim_best_model` stays), `DimBestVoter`, `DimensionLedger`'s blend rule (frozen-baseline
constraint), and the cluster/dimension accuracy fields.

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
