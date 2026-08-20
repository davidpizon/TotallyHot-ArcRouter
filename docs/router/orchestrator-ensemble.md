# The Orchestrator Ensemble (PLAN.md Phase L)

> **Status: shipped — 4 of 4 voters; `llm_router` is a documented substitute, not the paper's voter.**
> This doc is the owning narrative for PLAN.md's Phase L, moved here when the roadmap was pruned back
> to unfinished work per its own charter ("completed phases are removed rather than archived; the
> narrative for anything already shipped lives in the design doc that owns it"). Putting the ensemble
> on the live path was Phase M — see
> [`orchestrator-live-path-plan.md`](orchestrator-live-path-plan.md). The `logreg` voter described
> here was subsequently rebuilt around task embeddings by
> [`live-feedback-learning-plan.md`](live-feedback-learning-plan.md) Phases 3–4.

Four voters, weighted vote, argmax — research-doc §3.3 and A.1
([`../research/technical-reference.md`](../research/technical-reference.md)). Shipped as a
self-contained, DI-registered component: `OrchestratorRoutingPolicy` implements `IRoutingPolicy` and is
registered in `AddTotallyHotArcRouter`. Phase M later made it the default general-path policy inside
`CompositeRoutingPolicy`, gated by `RoutingOptions.EnableOrchestratorPolicy`.

## The four voters

- **`dim_best`** (`Router/Orchestrator/DimBestVoter.cs`) — looks up `DimensionModelScoreMatrix`'s
  probing-split prior (Phase K2's `BenchmarkDatabase`) for each candidate, preferring the live
  `RouterMemory.GetAverageScore` for the same (dimension, model) pair when one exists and falling back
  to the prior otherwise — live, execution-grounded feedback always wins once it exists. Tolerates an
  unsynced corpus (checks `BenchmarkDatabase.DatabasePath` for existence before opening a connection, so
  it never creates an empty database file as a side effect) by degrading to live-memory-only scoring,
  matching `CodeRouterBenchTable10ReconciliationTests`'s own handling of the same condition.
- **`memory_kNN`** (`Router/Orchestrator/MemoryKnnVoter.cs`) — calls `EmbeddingMemory.FindNearest`,
  computes the similarity-weighted average observed score per model among the neighbors restricted to
  current candidates, argmax. Abstains without a supplied task embedding or when no neighbor clears the
  similarity threshold.
- **`logreg`** — originally shipped as a TF-IDF one-vs-rest logistic regression over a checked-in
  placeholder artifact; **superseded by
  [`live-feedback-learning-plan.md`](live-feedback-learning-plan.md) Phases 3 and 6.**
  `Router/Orchestrator/LogRegVoter.cs` now scores `VotingContext.TaskEmbedding` against a locally
  trained `EmbeddingLogRegModelArtifact` and abstains cleanly when none exists.
  `CodeRouterBench/LogRegTrainer.cs` remains only as Phase N's static comparison baseline and trains
  from the OOD split — the only split with published task text (CodeRouterBench's ID task files carry
  only `task_id`/`split`/`source_split`/`dimension`). The hand-built placeholder artifact
  (`CodeRouterBench/Resources/logreg_voter_model.json`) was deleted — it had no remaining consumer.
- **`llm_router`** (`Router/Orchestrator/LlmRouterVoter.cs`) — **real, but a documented substitute for
  the paper's voter, agreed with the user ahead of implementation.** The paper's own fine-tuned
  Qwen3.5-0.8B checkpoint was never published anywhere (confirmed by search before implementation
  began), so it cannot be hosted regardless of runtime choice. This voter instead prompts a small,
  off-the-shelf, *un*-fine-tuned instruct model (Qwen2.5-0.5B-Instruct) locally via ONNX Runtime GenAI
  (`Microsoft.ML.OnnxRuntimeGenAI` — owns KV-cache/sampling internally, so no hand-rolled
  autoregressive-generation loop was needed), using research-doc Appendix B.3's **zero-shot** prompt
  (not the few-shot or +Perf-stats variants — those need oracle-labeled example curation from
  CodeRouterBench, a materially separate project) and a three-stage response-parsing fallback chain
  (JSON → fenced-block regex → model-name match; the paper's fourth stage, a hardcoded fallback model,
  becomes an abstention instead — see `LlmRouterVoter`'s remarks for why). No disagreement-gated
  invocation cost control — it runs on every decision like the other three voters, gated only by
  `RoutingOptions.EnableLlmRouterVoter`/`LlmRouterVoterWeight`. The default model artifact source
  (`Models/LlmRouterOptions.cs`) is a community-maintained Hugging Face export, not an official
  Microsoft one, pinned to its CPU build (`cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4`) because
  that repository publishes nothing at its root — every artifact sits under a per-execution-provider
  subfolder, and the graph's ~862 MB of weights are external, so `model.onnx.data` is a required
  download, not an optional one. If it ever stops resolving, the voter just abstains permanently — the
  exact "must degrade to a three-voter vote, never a hard failure, when the model artifact isn't
  present" path this phase originally required, still exercised, just no longer the *only* path this
  voter takes.

## Weights, logging, and the worked-example exit test

Voter weights and per-voter enablement are configuration (`RoutingOptions.DimBestVoterWeight` /
`MemoryKnnVoterWeight` / `LogRegVoterWeight` / `LlmRouterVoterWeight` and matching `Enable*Voter`
flags). `OrchestratorRoutingPolicy` logs the full vote breakdown into `RoutingDecision.CandidateScores`:
a per-model aggregate weighted score (what argmax runs over) plus every individual non-abstaining vote
keyed `voter:{voterName}:{modelName}` — "each voter's pick, each weighted score, the argmax" — via
static-template Serilog logging alongside it.

`OrchestratorRoutingPolicyTests.DecideAsync_ResearchDocWorkedExample_ResolvesToKimiK25AtWeightedScore1_47`
reproduces research-doc §3.3's worked example — voters picking MiniMax-M2.7 / GLM-5 / Kimi-K2.5 /
Kimi-K2.5 resolve to Kimi-K2.5 at weighted score 1.47 — with fakes standing in for all four voters. The
default voter weights (`dim_best` = 0.9, `memory_kNN` = 0.57, `logreg` = 0.43, `llm_router` = 0.64) are
a documented implementation choice sized to reproduce this exact example (0.9 + 0.57 = 1.47), not a
value the research doc publishes independently — see `RoutingOptions.DimBestVoterWeight`'s XML doc.
"Ensemble beats every single voter" is **not yet measured** — that half of the original exit criterion
carries forward to PLAN.md Phase N's regret harness.

## Admin surface: the "Local Voter Model" section

The `llm_router` voter's ONNX model artifacts are managed from the GUI, not only by ambient download:
`LlmRouterModelAdminGrpcService` (`Router/TextGeneration/LlmRouterModelAdminGrpcService.cs`, contract
`LlmRouterModelAdminService` in `src/Protos/telemetry.proto`) backs the Governance → Benchmark Data
panel's **"Local Voter Model"** section. It reports the voter model's file sync state, switches the
active model by base URL (`ILlmRouterModelOverrideStore`), and runs a sync with streamed per-file
progress (`LlmRouterModelSyncService`), mapped by `ProxyServer` onto the same loopback TLS endpoint as
`TelemetryService` and `BenchmarkDataAdminService`. GUI side: `Components/BenchmarkData.razor` +
`Services/LlmRouterModelStore.cs`. This is distinct from the **not-yet-built** `logreg` admin surface
([`live-feedback-learning-plan.md`](live-feedback-learning-plan.md) Phase 5) and from Phase M3's
read-only `RoutingModeAdminService`, which reports all four voters' enablement and weights.

## Settled deferral

`llm_router` prompts an off-the-shelf, un-fine-tuned small instruct model (Qwen2.5-0.5B-Instruct via
ONNX Runtime GenAI), not the paper's fine-tuned Qwen3.5-0.8B checkpoint — that checkpoint was never
published, so no implementation choice could have reproduced it; this is the plan's own documented
escape hatch for exactly that situation, agreed with the user ahead of implementation. Sub-deferrals
carried forward with it: zero-shot prompting only (no few-shot examples, no +Perf-stats ablation
variant), no disagreement-gated invocation, and a community-sourced (not officially
Microsoft-published) default model artifact URL — see `LlmRouterVoter`'s and `LlmRouterOptions`'s
remarks for the full reasoning on each. The `logreg` placeholder-artifact deferral originally recorded
alongside these is resolved and superseded by `live-feedback-learning-plan.md` (Phases 1–3, 6): the
live voter no longer reads a checked-in artifact at all (it scores task embeddings, abstaining cleanly
with none present), and `LogRegTrainer` / `LogRegTrainerReconciliationTests` now train Phase N's static
comparison baseline from the OOD split.
