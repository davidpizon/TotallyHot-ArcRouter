<div align="center">

# TotallyHot Arc Router

**Purpose-built model routing for coding workloads.**
</div>

TotallyHot Arc Router routes coding tasks to different backend models under a
performance-cost tradeoff. This repository contains the .NET (C#)
implementation of the router, which syncs the CodeRouterBench data it uses
into a local SQLite database on demand.

For the full CodeRouterBench dataset and paper, see the
[Hugging Face dataset](https://huggingface.co/datasets/Lance1573/CodeRouterBench)
and [arXiv:2606.22902](https://arxiv.org/abs/2606.22902).

## Data

CodeRouterBench is a task-by-model benchmark release published upstream by Zhou
et al. The tables are synced on demand into `coderouterbench.db` rather than
checked in - via Governance → Benchmark Data, the `sync_benchmark_data` MCP
tool, or `TotallyHotArcRouter --sync-benchmark-data`
(see [`../data/README.md`](../data/README.md) and
[`router/coderouterbench-sqlite-migration-plan.md`](router/coderouterbench-sqlite-migration-plan.md)
for what it syncs and verifies):

- `benchmark_id_results` (`split='probing'` union `split='id_test'`): 9,999 ID tasks x 8 models.
- `benchmark_ood_results`: 176 OOD tasks x 8 models.
- `benchmark_id_tasks` and `benchmark_ood_tasks`: task metadata.
- `benchmark_models`: canonical backend models and USD pricing.

`outputs/` and `agentic-artifacts/` are not restored yet - see
[`../data/README.md`](../data/README.md)'s "Not yet restored" section.

## Project Layout

```text
src/TotallyHotArcRouter*/             .NET router implementation, GUI, sandbox, tests
docs/                           Design docs and handbook

%LOCALAPPDATA%\TotallyHot.ArcRouter\coderouterbench.db   CodeRouterBench tables, synced on demand
```

## Design docs

The roadmap of remaining work, in order, is [`../src/PLAN.md`](../src/PLAN.md). Every document under
[`router/`](router/) is indexed below — this table is **exhaustive**, so a new router doc belongs here
too. GUI docs live in [`gui/`](gui/) and are not indexed here.

Status shown is each doc's own, verified against the code during the reconciliation pass recorded in
[`router/doc-code-reconciliation-plan.md`](router/doc-code-reconciliation-plan.md).

### Routing and learning

| Doc | What it owns | Status |
|---|---|---|
| [`router/utility-model-routing.md`](router/utility-model-routing.md) | Classifier, `IRoutingPolicy`, cost-aware utility routing | Shipped (H, I) |
| [`router/orchestrator-ensemble.md`](router/orchestrator-ensemble.md) | The five-voter Orchestrator ensemble and its weights | Shipped (5 of 5 voters) |
| [`router/orchestrator-live-path-plan.md`](router/orchestrator-live-path-plan.md) | Orchestrator on the live path; requested-vs-routed end to end | Shipped (M1–M4) |
| [`router/phase-m2-plan.md`](router/phase-m2-plan.md) | Requested-vs-routed telemetry, the M2 slice in detail | Implemented |
| [`router/memory-persistence.md`](router/memory-persistence.md) | `RouterMemory` / `EmbeddingMemory` SQLite persistence | Shipped |
| [`router/live-feedback-learning-plan.md`](router/live-feedback-learning-plan.md) | Live feedback capture, embedding-backed `logreg`, its trainer and admin surface | Phases 1–5 shipped; 6 partial |
| [`router/self-organizing-classification-plan.md`](router/self-organizing-classification-plan.md) | Transcripts, clustering, the `cluster_best` voter, adaptive-routing toggle | Shipped (T1–T6) |
| [`router/routing-roi-regret-plan.md`](router/routing-roi-regret-plan.md) | Routing ROI: expense and regret vs `dim_best`, fast drain, hard pause under load | Shipped |
| [`router/agent-resilience-strategies.md`](router/agent-resilience-strategies.md) | Circuit breaker and failover ranking; leaky bucket | Circuit breaker shipped; leaky bucket not built |
| [`router/model-identity-canonicalization.md`](router/model-identity-canonicalization.md) | `ModelNameCanonicalizer` — spelling vs. identity | Implemented |

### Benchmark data and evaluation

| Doc | What it owns | Status |
|---|---|---|
| [`router/coderouterbench-sqlite-migration-plan.md`](router/coderouterbench-sqlite-migration-plan.md) | Benchmark corpus sync into SQLite, checksums, row counts | Shipped (all six phases) |
| [`router/regret-evaluation-harness-plan.md`](router/regret-evaluation-harness-plan.md) | PLAN.md Phase N: `CumReg`/`AvgPerf`/`TotTok`/`$Total`/`Perf/$`, replay engine, comparison baselines | N1–N3 shipped; N4–N6 remain |
| [`router/geval-shadow-scoring-plan.md`](router/geval-shadow-scoring-plan.md) | G-Eval shadow judge, then judge-as-verifier for non-executable dimensions | G1 shipped; G2–G3 proposed |
| [`router/sandboxed-executor-architecture.md`](router/sandboxed-executor-architecture.md) | The Verifier: tiered sandboxed execution and scoring | Implemented (Phases 1–6) |

### Telemetry, cost, and pricing

| Doc | What it owns | Status |
|---|---|---|
| [`router/telemetry.md`](router/telemetry.md) | Telemetry pipeline, transport, and field provenance | Implemented, test-verified |
| [`router/grpc-migration.md`](router/grpc-migration.md) | Telemetry transport SignalR → gRPC | Implemented, narrower than designed |
| [`router/signalr-hub-security.md`](router/signalr-hub-security.md) | TLS + auth for the telemetry hub | Historical — SignalR removed; superseded by gRPC |
| [`router/agent-cost-tracking.md`](router/agent-cost-tracking.md) | Persistent spend ledger, auto-refreshed pricing, provider reconciliation | Superseded by the shipped implementation |
| [`router/token-tracking-improvements.md`](router/token-tracking-improvements.md) | Survey of external usage trackers; the analysis behind the implementation plan | Implemented |
| [`router/token-tracking-implementation-plan.md`](router/token-tracking-implementation-plan.md) | Phase-by-phase execution of the analysis above | Implemented (all six phases) |
| [`router/anthropic-reported-usage-plan.md`](router/anthropic-reported-usage-plan.md) | Cache-aware Anthropic usage and budget tracking | Implemented (Phases 1–3) |
| [`router/openai-format-usage-accuracy-plan.md`](router/openai-format-usage-accuracy-plan.md) | Usage accuracy for OpenAI-format traffic, incl. Anthropic-behind-OpenAI | Implemented (Phases 1–3) |
| [`router/model-price-catalog.md`](router/model-price-catalog.md) | Multi-aggregator price ingestion, resolution, runtime cache | Phases 1–4 implemented |
| [`router/d3-alias-resolution.md`](router/d3-alias-resolution.md) | Mapping aggregator model names onto the router key | Implemented (incl. Slice 4) |
| [`router/pricing-seed-removal.md`](router/pricing-seed-removal.md) | Removing the fake `Pricing` seed; "unknown" as the honest default | Implemented |

### API translation and tool calling

| Doc | What it owns | Status |
|---|---|---|
| [`router/unified-api-translation.md`](router/unified-api-translation.md) | Ollama, Gemini, Anthropic, and Bedrock translation | All four providers implemented |
| [`router/tool-call-normalization.md`](router/tool-call-normalization.md) | Per-model tool-call dialect detection and normalization | Phases 0–5, 8 shipped; 6 partial; 7 proposed |

### Management, security, and operations

| Doc | What it owns | Status |
|---|---|---|
| [`router/mcp-endpoint.md`](router/mcp-endpoint.md) | The MCP management endpoint as built — ports, auth, tools | Reference (as-built) |
| [`router/mcp-endpoint-plan.md`](router/mcp-endpoint-plan.md) | MCP endpoint + hardened REST over one shared `ManagementFacade` | Implemented |
| [`router/secrets-at-rest.md`](router/secrets-at-rest.md) | The protected secret store as built | Reference (as-built) |
| [`router/secrets-at-rest-plan.md`](router/secrets-at-rest-plan.md) | Generic protected store; Anthropic Admin-API path as first consumer | Implemented (all six phases) |
| [`router/security-hardening-plan.md`](router/security-hardening-plan.md) | Threat model and prioritized remediation findings | Action list — per-finding status unverified |
| [`router/serilog-logging-guide.md`](router/serilog-logging-guide.md) | Config-driven Serilog setup and sink options | Partially implemented — Console sink only |
| [`router/system-proxy-architecture.md`](router/system-proxy-architecture.md) | OS-level proxy registration and upstream chaining | Proposed — not implemented |
| [`router/proxy-coexistence.md`](router/proxy-coexistence.md) | Detecting, backing up, and restoring existing proxy settings | Proposed — not implemented |

### Working documents

| Doc | What it owns |
|---|---|
| [`router/backlog.md`](router/backlog.md) | Router-side known defects and not-yet-implemented work |
| [`router/tracked-todos.md`](router/tracked-todos.md) | Open working items carried across sessions |
| [`router/doc-code-reconciliation-plan.md`](router/doc-code-reconciliation-plan.md) | Doc/code drift audit and the plan to close it |

## Citation

```bibtex
@article{agent2026zhou,
  title         = {Agent-as-a-Router: Agentic Model Routing for Coding Tasks},
  author        = {Pengfei Zhou, Zhiwei Tang, Yixing Ma, Jiasheng Tang, Yizeng Han, Zhenglin Wan, Fanqing Meng, Wei Wang, Bohan Zhuang, Wangbo Zhao, Yang You},
  journal       = {arXiv preprint arXiv:2606.22902},
  year          = {2026},
  archivePrefix = {arXiv},
  eprint        = {2606.22902},
  url           = {https://arxiv.org/abs/2606.22902}
}
```

