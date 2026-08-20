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

The roadmap of remaining work, in order, is [`../src/PLAN.md`](../src/PLAN.md). Key router docs
(the full set lives in [`router/`](router/); GUI docs in [`gui/`](gui/)):

| Doc | What it owns |
|---|---|
| [`router/utility-model-routing.md`](router/utility-model-routing.md) | Classifier, `IRoutingPolicy`, cost-aware utility routing (shipped) |
| [`router/memory-persistence.md`](router/memory-persistence.md) | `RouterMemory` / `EmbeddingMemory` SQLite persistence (shipped) |
| [`router/coderouterbench-sqlite-migration-plan.md`](router/coderouterbench-sqlite-migration-plan.md) | Benchmark corpus sync into SQLite (shipped) |
| [`router/orchestrator-ensemble.md`](router/orchestrator-ensemble.md) | The four-voter Orchestrator ensemble (shipped) |
| [`router/orchestrator-live-path-plan.md`](router/orchestrator-live-path-plan.md) | Orchestrator on the live path, requested-vs-routed (shipped, M1–M4) |
| [`router/live-feedback-learning-plan.md`](router/live-feedback-learning-plan.md) | Live feedback capture + embedding-backed `logreg` (Phases 1–4 shipped; 5–6 open) |
| [`router/self-organizing-classification-plan.md`](router/self-organizing-classification-plan.md) | Transcripts, clustering, `cluster_best` voter (proposed, T1–T6) |
| [`router/geval-shadow-scoring-plan.md`](router/geval-shadow-scoring-plan.md) | G-Eval shadow judge and judge-verifier (proposed, G1–G3) |
| [`router/regret-evaluation-harness-plan.md`](router/regret-evaluation-harness-plan.md) | PLAN.md Phase N's regret harness spec (proposed) |
| [`router/tool-call-normalization.md`](router/tool-call-normalization.md) | Per-model tool-call dialect detection/normalization (Phases 0–5, 8 shipped) |
| [`router/telemetry.md`](router/telemetry.md) | Telemetry pipeline and field provenance |
| [`router/backlog.md`](router/backlog.md) / [`router/tracked-todos.md`](router/tracked-todos.md) | Known defects / open working items |

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

