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

