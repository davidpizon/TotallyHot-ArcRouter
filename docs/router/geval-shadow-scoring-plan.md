# G-Eval Shadow Scoring and Judge-Verifier Plan

Status: **Proposed** (plan only — no phase has started). **Ordering** (per `src/PLAN.md`'s "Remaining
work, in order"): G1 lands after the `self-organizing-classification-plan.md` T phases and *before*
PLAN.md Phase N — the shadow table costs nothing on the hot path and G2's gate needs volume, so
agreement data accumulates passively while the harness is built. G2 and G3 follow Phase N, gated on
that accumulated data.

This plan adds an LLM-as-judge scorer, built on the G-Eval recipe
([docs/research/2303.16634v3.md](../research/2303.16634v3.md)), to the router's scoring pipeline in
two deliberately separated stages:

1. **Shadow mode (G1/G2)** — the judge scores requests *in parallel* with the existing
   `VerifierScorer`, records its opinion in a side table, and influences nothing. This measures, on
   this operator's real traffic, how often the judge agrees with execution-grounded scores before it
   is trusted with anything.
2. **Alternate verifier for non-executable dimensions (G3)** — gated on G2's results, the judge
   becomes the scorer of record *only* for dimensions the sandbox cannot execute, where today's
   score collapses to a syntax check.

Two cross-cutting requirements apply from the first phase:

- **Raw response text is preserved only until the judge has evaluated it** (ephemeral by default;
  see §Raw-text preservation).
- **Every judge-graded score carries a provenance marker** (`is_judge_scored`) so the learning
  layers can weight, discount, or exclude judge-graded rows later (see §Provenance).

## Why

- **The Verifier is blind on non-executable content.** `VerifierScorer.Score`
  (`src/TotallyHotArcRouter.Sandbox/Scoring/VerifierScorer.cs`) folds the execution weight into the
  syntax weight when `result.Executed` is false, so a prose answer — an algorithm explanation, a
  design review, any non-coding dimension — is scored on syntax validity alone. A brilliant answer
  and a useless one receive the same `u_i`.
- **Naive LLM-as-judge is measurably noisy.** `src/PLAN.md`'s Settled deferrals (full evidence in
  `data/README.md`'s "Known data-fidelity limit" section) document per-cell
  divergences up to 0.32 in the judge-scored CodeRouterBench dimensions (`algorithm`,
  `test_generation`), attributed to run-to-run LLM-as-Judge noise baked into the released CSV.
  G-Eval's probability-weighted scoring exists precisely to reduce that quantization/variance noise
  and yields a continuous score that maps directly onto the `u_i ∈ [0,1]` shape every downstream
  consumer (`EmbeddingMemory`, `DimensionModelScoreMatrix`, the logreg heads) already expects.
- **Judge bias is real and already has a standing rule here.** G-Eval's own analysis shows LLM
  judges systematically over-score LLM-generated text even against human preference.
  `regret-evaluation-harness-plan.md`'s ground rule ("no fabricated training data" extends to
  fabricated *evaluation* data) is why shadow mode comes first and why provenance is mandatory: a
  judge-graded row must never be indistinguishable from an execution-grounded one.

## Ground rules

- **The routing hot path never blocks on judging.** The judge runs seconds per score; the observer
  chain must hand it work through a bounded background queue and return immediately (the same
  posture `self-organizing-classification-plan.md` sets for all learning writes).
- **Dollar-free is not cost-free.** The judge backbone is a locally served model (no paid backends
  for evaluation — the standing constraint from `regret-evaluation-harness-plan.md`), but local
  inference still costs compute and wall-clock; the queue is bounded and sheds load rather than
  backing up.
- **Shadow mode influences nothing.** Until G3, the judge's score never touches
  `SandboxResult.UnifiedScore`, never reaches `memory_entries.score`, and never feeds any voter.
- **Judge scores are never a training reward without provenance.** Any layer that consumes
  `memory_entries` must be able to filter on `is_judge_scored`.
- **Raw text does not outlive its purpose.** The router's memory must not become a transcript store
  (the standing decision in `live-feedback-learning-plan.md`); response text held for judging is
  discarded the moment the judge scores it or its TTL expires.
- Repository conventions apply throughout: zero build warnings, XML docs on every public member,
  Serilog with static message templates, ≥80% coverage, no test over 5 seconds, Mermaid diagrams.

## Architecture

```mermaid
flowchart TD
    subgraph HotPath["Hot path (unchanged)"]
        PM["ProxyMiddleware<br/>captures response body;<br/>ResponseTextExtractor already<br/>extracts reply text"]
        SE["SandboxExecutor.ExecuteAsync<br/>VerifierScorer -> UnifiedScore"]
        CO["CompositeRouterScoreObserver<br/>(fans out, exception-tolerant)"]
        PM --> SE --> CO
    end

    subgraph Existing["Existing observers"]
        RMO["RouterMemoryScoreObserver"]
        EMO["EmbeddingMemoryScoreObserver<br/>-> memory_entries"]
    end

    subgraph New["G1: shadow judge (new)"]
        PRC["PendingResponseTextCache<br/>(ephemeral, TTL + capacity capped)"]
        JSO["JudgeShadowScoreObserver<br/>enqueue + return immediately"]
        Q["Bounded background queue<br/>(hosted service)"]
        GE["G-Eval judge call<br/>local model, logprob-weighted"]
        JT[("judge_shadow_scores<br/>side table")]
        PM -.->|"response text by<br/>correlation id"| PRC
        JSO --> Q --> GE --> JT
        PRC -->|"TryTake on judge run;<br/>text discarded after"| GE
    end

    CO --> RMO
    CO --> EMO
    CO --> JSO
```

The seam is `IRouterScoreObserver`: `CompositeRouterScoreObserver`
(`src/TotallyHotArcRouter/Router/CompositeRouterScoreObserver.cs`) already fans a scored
`SandboxResult` out to each registered observer and swallows individual failures, so a third,
best-effort observer is additive — no existing type changes shape.

## Raw-text preservation (hybrid)

**Requirement:** the agent's raw response text must survive from response completion until the
judge has had a chance to evaluate it — and no longer.

**Default path — ephemeral cache.** A new `PendingResponseTextCache` mirrors
`PendingTaskEmbeddingCache`'s design exactly: keyed by correlation id, TTL-bounded, capacity-bounded,
`TryTake` consumes the slot. It is populated in `ProxyMiddleware` at the same point
`ResponseTextExtractor.TryExtractText` already runs over the buffered response body (the text is
already in hand there — this adds retention, not parsing). The judge's background worker `TryTake`s
the text when it dequeues the job; whether judging succeeds, fails, or the entry ages out, the text
is gone from process memory afterward. Nothing is written to disk. A missing entry (TTL expiry,
capacity eviction, capture raced) is a lost judging opportunity, not an error — logged and dropped,
matching every other best-effort observation path.

**Bounded memory:** worst case is (in-flight unjudged responses × capped text size). The cache caps
per-entry text at a configurable byte limit (default aligned with
`SandboxOptions.MaxCapturedOutputBytes`'s philosophy) and total entries at a configurable capacity,
so the ceiling is a few MB regardless of traffic.

**Secondary path — transcript backfill (only when T1 is enabled).** When
`self-organizing-classification-plan.md` Phase T1's opt-in transcript capture exists and is enabled,
the judge worker may additionally read `prompt_text`/`response_text` from `transcripts.db` to score
rows whose ephemeral entry was missed, and to backfill judge scores for recent historical rows. This
path is strictly optional: G1 works with transcript capture off, and this plan adds no new
persistence of raw text anywhere.

## Provenance: `is_judge_scored`

A new boolean column, following the additive-migration convention `RouterMemoryDatabase` and
`PriceCatalogDatabase.MigrateEnabledColumn` already use:

- **`memory_entries.is_judge_scored`** (INTEGER 0/1, default 0). Existing rows backfill as 0 —
  every row written before this plan is execution-grounded by construction. `MemoryEntry` gains the
  matching property; `EmbeddingMemory.AddEntryAsync` threads it through.
- **Transcript row `is_judge_scored`** (when T1's `transcripts.db` exists) — same semantics, set on
  score backfill.

Semantics: `1` means the `score` on this row was produced by the LLM judge rather than by
`VerifierScorer`'s structural/execution signals. In G1/G2 no row ever has `is_judge_scored = 1`
(shadow scores live only in the side table); the column lands early anyway so that every learning
consumer (`MemoryKnnVoter`, `EmbeddingLogRegTrainer`, T2's clustering, T4's comparison) can be
written/updated against the final schema once, and can weight, discount, or exclude judge-graded
rows from the day G3 first writes one.

Sibling precedent: this is exactly the pattern T1 already sets for `IsExploratory` — a per-row
provenance bit that exists so later consumers can separate populations instead of discovering too
late that they can't.

## Phase map

| Phase | Deliverable | Depends on | Status |
|---|---|---|---|
| G1 | Shadow judge observer, `PendingResponseTextCache`, `judge_shadow_scores` side table, `is_judge_scored` columns (always 0) | none (T1 optional) | Proposed |
| G2 | Agreement/calibration analysis surface over the shadow table; go/no-go criteria for G3 | G1 + accumulated shadow data | Proposed |
| G3 | Judge as scorer of record for non-executable dimensions; first `is_judge_scored = 1` rows | G2 gate passed | Proposed |

---

## Phase G1 — Shadow judge observer

**1a. Judge backbone.** A configurable, locally served OpenAI-compatible endpoint (LM Studio /
llama.cpp / Ollama — the operator chooses; the plan assumes only "local and free"). Prefer token
logprobs for G-Eval's probability weighting — one inference call per score; fall back to the paper's
n-sample estimation only when the serving stack exposes no logprobs, with the sample count
configurable and defaulting low. Prompts follow the G-Eval recipe verbatim (task introduction +
per-dimension criteria + cached auto-CoT steps + form-filling cue); the auto-CoT is generated once
per dimension and cached with the artifact conventions the codebase already uses (embedding-model
and prompt-version guards, mirroring the trained-artifact guards in
`self-organizing-classification-plan.md`).

**1b. `PendingResponseTextCache`.** As specified in §Raw-text preservation. Registered in DI beside
`PendingTaskEmbeddingCache`; populated in `ProxyMiddleware` where response text is already
extracted; options for TTL, capacity, and per-entry byte cap under a new `JudgeOptions` section,
all off unless `JudgeShadowEnabled` is true (default **false** — enabling the judge is a deliberate
choice, the same posture as T1's capture toggle).

**1c. `JudgeShadowScoreObserver`.** An `IRouterScoreObserver` registered as a third element of the
`CompositeRouterScoreObserver` list. `ObserveAsync` does two cheap things and returns: snapshot the
fields it needs from the `SandboxResult` (correlation id, dimension, model, `UnifiedScore`), and
enqueue onto a bounded channel. When the channel is full, the job is dropped with a debug log —
shed, never block. A hosted service drains the channel: `TryTake` the response text, run the G-Eval
call against the configured backbone, write one row to the side table, discard the text.

**1d. `judge_shadow_scores` side table.** Own SQLite table (in the router-memory database, additive
migration): `id`, `correlation_id`, `created_at_utc`, `dimension`, `model`, `verifier_score`,
`judge_score`, `judge_model`, `judge_prompt_version`, `judge_latency_ms`, `used_logprobs` (0/1),
`executed` (0/1 — whether the verifier's score was execution-grounded or Tier-0-only, the single
most important split for G2). Retention: same startup-plus-periodic purge pattern as T1e, bounded
by `RetentionDays`/`MaxRows`.

**1e. `is_judge_scored` columns.** As specified in §Provenance — landed here, always written 0.

**Exit:** with `JudgeShadowEnabled`, one scored request produces at most one shadow row and
`UnifiedScore`/`memory_entries` are byte-identical to a run with the judge disabled (asserted by
test); with it disabled (default), no cache, no queue, no table writes; response text is
demonstrably absent from the cache after judging and after TTL expiry; a full channel sheds without
affecting routing latency; all migrations are additive and re-runnable.

## Phase G2 — Calibration analysis and the G3 gate

Runs after G1 has accumulated shadow data on real traffic (minimum row count configurable; no fixed
calendar time).

- **Agreement analysis**, split by the `executed` flag and by dimension: rank correlation
  (Spearman) and mean absolute difference between `judge_score` and `verifier_score` where both are
  meaningful, plus score-distribution shape (is the judge collapsing to one value? G-Eval's known
  failure without probability weighting).
- **Self-preference probe**: per-model mean judge score vs. per-model mean verifier score — a judge
  that systematically inflates one model family relative to its execution-grounded scores exhibits
  exactly the bias the paper warns about, quantified on local traffic.
- **Surface**: a read-only admin/gRPC status view (following the trainer-status precedent in
  `self-organizing-classification-plan.md` T5), not a GUI build-out — numbers first.

**Gate for G3 (all required):** (1) on *executed* rows, judge rank-correlates with the verifier at
or above a configured floor — the judge must at least reproduce ground truth where ground truth
exists; (2) judge score distribution is non-degenerate (variance floor); (3) measured
self-preference skew is below a configured ceiling. Failing the gate loops back to prompt/backbone
iteration in G1 — it does not soften the gate.

## Phase G3 — Judge as alternate verifier for non-executable dimensions

Gated on G2. Scope is deliberately narrow: **only dimensions/results where the sandbox produced no
execution signal** (`result.Executed == false` and the dimension is configured judge-eligible).
Executable dimensions keep `VerifierScorer` unconditionally — where ground truth is available, an
opinion never replaces it.

- A judge-eligible, non-executed result's `u_i` becomes the G-Eval score (normalized to `[0,1]`),
  written through the existing observer path with `is_judge_scored = 1` on the `memory_entries` row
  (and the transcript row when present).
- The shadow table keeps recording in parallel — G2's analysis becomes a permanent regression check
  rather than a one-time gate.
- Learning-layer handling lands in the same phase, not later: `MemoryKnnVoter` and the logreg/
  clustering trainers gain a configurable policy for judge-graded rows — include, exclude, or
  down-weight (default: **down-weight**, weight configurable) — so the first `is_judge_scored = 1`
  row that exists is already handled deliberately by every consumer.

**Exit:** a non-executable, judge-eligible request produces a `memory_entries` row whose score came
from the judge and whose `is_judge_scored` is 1; an executable request is provably untouched by the
judge path; every learning consumer honors the configured judge-row policy under test; disabling
the judge cleanly reverts non-executable dimensions to today's Tier-0 behavior.

## Non-goals

- Judge scores as a training reward for any model tuning — out of scope permanently, per the
  regret plan's ground rule.
- Any paid or remote judging backend.
- Persisting raw prompt/response text anywhere new — the only persistent text store remains T1's
  opt-in `transcripts.db`, owned by that plan.
- Replacing `VerifierScorer` on executable dimensions, in any phase.

## Key references

- [docs/research/2303.16634v3.md](../research/2303.16634v3.md) — the G-Eval recipe, its
  probability-weighted scoring, and the self-preference bias analysis this plan's gates encode.
- `docs/router/self-organizing-classification-plan.md` — T1 transcript capture (optional secondary
  text source; `IsExploratory` provenance precedent), trainer/artifact conventions.
- `docs/router/regret-evaluation-harness-plan.md` — the no-fabricated-evaluation-data ground rule
  and the no-paid-backend constraint.
- `src/TotallyHotArcRouter.Sandbox/Scoring/VerifierScorer.cs` — the non-executed fold this plan's
  G3 addresses.
- `src/TotallyHotArcRouter/Router/CompositeRouterScoreObserver.cs` — the observer seam G1 plugs
  into.
- `src/TotallyHotArcRouter/Telemetry/ResponseTextExtractor.cs` and
  `src/TotallyHotArcRouter/Proxy/ProxyMiddleware.cs` — where response text already exists in hand
  for the ephemeral cache.
- `src/PLAN.md` Settled deferrals + `data/README.md`'s "Known data-fidelity limit" — the measured
  LLM-as-Judge noise motivating probability weighting.
