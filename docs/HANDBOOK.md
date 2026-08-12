<div align="center">

# TotallyHot Arc Router

**Purpose-built model routing for coding workloads.**

</div>

This repository also releases **CodeRouterBench**, the benchmark suite used to
evaluate agentic model routing across in-distribution coding tasks and the
current OOD176 agentic-programming task stream.

Paper page: [Hugging Face Daily Papers](https://huggingface.co/papers/2606.22902)
and [arXiv:2606.22902](https://arxiv.org/abs/2606.22902).

TotallyHot Arc Router is a model-routing framework for comparing an
adaptive router against single-model, heuristic, online-bandit, retrieval,
and trained-policy baselines on coding tasks.

**Note:** The sections below describe what remains: the fetch-on-demand CodeRouterBench data
(PLAN.md Phase K) and the runtime integration. `outputs/`, `agentic-artifacts/`, and the nested source
matrices the original release also published (`data/matrices/`, `data/id/`, `data/ood/`) are **not**
restored - see `data/README.md`'s "Not yet restored" section.

The current public OOD benchmark is **OOD176**. The older OOD112/SWE-MiniSandbox reproduction that the
upstream dataset also publishes is not restored here.

## CodeRouterBench Dataset

**CodeRouterBench** is the benchmark release, not a router output dump. Its core tables are complete
task-by-model result matrices, fetched on demand into `data/coderouterbench/` by
`scripts/fetch-coderouterbench.sh` (see `data/README.md`) rather than checked in:

- `data/coderouterbench/id_results_long.csv`: 9,999 ID tasks x 8 backend models
  = 79,992 result rows.
- `data/coderouterbench/id_probing_results_long.csv`: 7,080 probing tasks x 8
  backend models = 56,640 result rows. This is the merged original train +
  validation set.
- `data/coderouterbench/id_test_results_long.csv`: 2,919 ID test tasks x 8
  backend models = 23,352 result rows.
- `data/coderouterbench/ood176_results_long.csv`: 176 OOD tasks x 8 backend
  models = 1,408 result rows.
- `data/coderouterbench/id_tasks.jsonl` and
  `data/coderouterbench/ood176_tasks.jsonl`: task metadata.
- `data/coderouterbench/models.json`: canonical model list and pricing
  metadata.
- `data/coderouterbench/summary.json`: integrity counts and source paths, as published.

Each result row records the task id, model, score or pass signal, cost, and
token/latency or verifier metadata when available, as computed by the upstream dataset. This
repository's own `TotallyHot.ArcRouter.CodeRouterBench.CodeRouterBenchCsvReader` reads these tables
directly; it does not recompute `cost_usd` or re-derive rows from any nested source matrix, since those
matrices are not restored here (see above).

The tables above were originally rebuilt from nested source matrices by
`scripts/export_coderouterbench.py`, which no longer exists in this repository or the upstream
release's Python pipeline; the published tables are consumed as-is.

## Repository Layout

```text
src/TotallyHotArcRouter*/              .NET router implementation, GUI, sandbox, tests
scripts/                         Data-fetch scripts (not a build/test pipeline)

data/coderouterbench/            CodeRouterBench task x model tables, fetched on demand (gitignored)
```

## CodeRouterBench Data

The canonical public benchmark files, once fetched, live in `data/coderouterbench/`:

- `id_results_long.csv`: one row per ID task/model result.
- `id_probing_results_long.csv`: original train + validation merged into the
  probing set.
- `id_test_results_long.csv`: held-out ID test set, labeled `id_test`.
- `ood176_results_long.csv`: one row per OOD176 task/model result.
- `id_tasks.jsonl` and `ood176_tasks.jsonl`: task metadata.
- `models.json`: the eight canonical backend models and USD pricing metadata.
- `summary.json`: integrity counts and source paths, as published.

Rebuilding these matrices, and republishing the dataset or the trained router
adapter to Hugging Face, previously used `scripts/build_ood176_dataset.py` and
two upload shell scripts. All of that tooling has been removed along with the
rest of the Python pipeline; republishing would need it restored first.

## Notes And Caveats

- No API keys are required. The release does not call external model APIs.
- The Python maintainer scripts that rebuilt compact data from raw local
  experiment outputs have been removed along with the rest of the Python
  pipeline.
- `data/README.md` documents a known data-fidelity limit: individual `bug_fixing`, `algorithm`, and
  `test_generation` cells for GLM-5, Qwen3-Max, Qwen3.5-Plus, and MiniMax-M2.7 diverge from the
  published research-doc Table 10 by up to 0.32, though per-model row averages (AvgPerf) match to
  within 0.05 for every model. This is a settled deferral (PLAN.md Phase K), not an open bug.

## Citation

If you use this bundle, please cite the associated arXiv paper.

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

