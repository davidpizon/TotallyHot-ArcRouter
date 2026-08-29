<div align="center">

# TotallyHot Arc Router

**Purpose-built model routing for coding workloads.**

</div>

This repository **evaluates against CodeRouterBench**, the benchmark suite used to
evaluate agentic model routing across in-distribution coding tasks and the
current OOD176 agentic-programming task stream. CodeRouterBench is published by
Zhou et al. (not by this project) under the MIT license; this repository consumes
it as an external dependency and neither hosts nor republishes it.

Paper page: [Hugging Face Daily Papers](https://huggingface.co/papers/2606.22902)
and [arXiv:2606.22902](https://arxiv.org/abs/2606.22902).

TotallyHot Arc Router is a model-routing framework for comparing an
adaptive router against single-model, heuristic, online-bandit, retrieval,
and trained-policy baselines on coding tasks.

**Note:** The sections below describe what remains: the sync-on-demand CodeRouterBench data (PLAN.md
Phase K, retargeted onto a local SQLite database by
docs/router/coderouterbench-sqlite-migration-plan.md) and the runtime integration. `outputs/`,
`agentic-artifacts/`, and the nested source matrices the original release also published
(`data/matrices/`, `data/id/`, `data/ood/`) are **not** restored - see `data/README.md`'s "Not yet
restored" section.

The current public OOD benchmark is **OOD176**. The older OOD112/SWE-MiniSandbox reproduction that the
upstream dataset also publishes is not restored here.

## CodeRouterBench Dataset

**CodeRouterBench** is an upstream benchmark release by Zhou et al., not a router output dump and not
this project's to relicense. Its core tables are complete
task-by-model result matrices, synced on demand into a local SQLite database (`coderouterbench.db`) via
Governance → Benchmark Data, the `sync_benchmark_data` MCP tool, or `--sync-benchmark-data`
(docs/router/coderouterbench-sqlite-migration-plan.md) rather than checked in:

- `benchmark_id_results` (`split='probing'` union `split='id_test'`): 9,999 ID tasks x 8 backend models
  = 79,992 result rows. `id_results_long.csv` is this query, not a separately stored file.
- `benchmark_id_results` (`split='probing'`): 7,080 probing tasks x 8 backend models = 56,640 result
  rows. This is the merged original train + validation set.
- `benchmark_id_results` (`split='id_test'`): 2,919 ID test tasks x 8 backend models = 23,352 result
  rows.
- `benchmark_ood_results`: 176 OOD tasks x 8 backend models = 1,408 result rows.
- `benchmark_id_tasks` and `benchmark_ood_tasks`: task metadata.
- `benchmark_models`: canonical model list and pricing metadata.
- `benchmark_summary`: integrity counts and source paths, as published.

Each result row records the task id, model, score or pass signal, cost, and
token/latency or verifier metadata when available, as computed by the upstream dataset. This
repository's own `TotallyHot.ArcRouter.CodeRouterBench.DimensionModelScoreMatrix.FromDatabase` reads these
tables directly; it does not recompute `cost_usd` or re-derive rows from any nested source matrix, since
those matrices are not restored here (see above).

The tables above were originally derived upstream from nested source matrices that the current
release no longer publishes; the published tables are consumed as-is.

## Repository Layout

```text
src/TotallyHotArcRouter*/              .NET router implementation, GUI, quality verifier, tests

%ProgramData%\TotallyHotArcRouter\coderouterbench.db   CodeRouterBench task x model tables, synced on demand
```

## CodeRouterBench Data

The canonical public benchmark files, once synced, live in `coderouterbench.db`:

- `benchmark_id_results`: one row per ID task/model result, `split`-discriminated (`probing`/`id_test`).
- `benchmark_ood_results`: one row per OOD176 task/model result.
- `benchmark_id_tasks` and `benchmark_ood_tasks`: task metadata.
- `benchmark_models`: the eight canonical backend models and USD pricing metadata.
- `benchmark_summary`: integrity counts and source paths, as published.

Rebuilding or republishing these matrices is not this project's to do - CodeRouterBench is published
upstream by Zhou et al., and this repository is a consumer of it.

## Notes And Caveats

- No API keys are required. The release does not call external model APIs.
- `data/README.md` (and docs/router/coderouterbench-sqlite-migration-plan.md) documents a known
  data-fidelity limit: individual `bug_fixing`, `algorithm`, and
  `test_generation` cells for GLM-5, Qwen3-Max, Qwen3.5-Plus, and MiniMax-M2.7 diverge from the
  published research-doc Table 10 by up to 0.32, though per-model row averages (AvgPerf) match to
  within 0.05 for every model. This is a settled deferral (PLAN.md's "Settled deferrals" list; full
  evidence in `data/README.md`), not an open bug.

## License

TotallyHot Arc Router is licensed under the [GNU Affero General Public License v3.0](../LICENSE),
with an [additional permission](../LICENSE.exceptions.md) for Microsoft platform components.
Third-party attribution is in [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

## Citation

The routing approach and the CodeRouterBench dataset are the work of Zhou et al. If you use either,
cite their paper.

```bibtex
@article{agent2026zhou,
  title         = {Agent-as-a-Router: Agentic Model Routing for Coding Tasks},
  author        = {Pengfei Zhou, Zhiwei Tang, Yixing Ma, Jiasheng Tang, Yizeng Han, Zhenglin Wan, Fanqing Meng, Wei Wang, Bohan Zhuang, Wangbo Zhao, Yang You},
  journal       = {arXiv preprint arXiv:2606.22902},
  year          = {2026},
  archivePrefix = {arXiv},
  eprint        = {2606.22902},
  url           = {https://arxiv.org/abs/2606.22902},
  note          = {Hugging Face Daily Papers: https://huggingface.co/papers/2606.22902},
}
```

