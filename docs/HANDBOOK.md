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

**Note:** The sections below describe what remains: the checked-in CodeRouterBench data, reference outputs, and the runtime integration.

The current public OOD benchmark in the checked-in data is **OOD176**. The
older OOD112/SWE-MiniSandbox reproduction is kept only as a legacy supplement
and is documented under `data/README.md` and the agentic artifact evidence
tables.

## CodeRouterBench Dataset

**CodeRouterBench** is the benchmark release, not a router output dump. Its
core tables are complete task-by-model result matrices:

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
- `data/coderouterbench/summary.json`: integrity counts and source paths.

Each result row records the task id, model, score or pass signal, cost, and
token/latency or verifier metadata when available. TotallyHotArcRouter decisions,
baseline traces, and paper tables are derived from these matrices and live
under `outputs/`.

For ID rows, `cost_usd` is computed from `data/id/tokens.jsonl` and
`data/matrices/phase1_id/model_pricing.json`; it is not copied from the legacy
compact observation matrix. Rows without token records are marked with
`cost_source=missing_token_record_zero_total` when the compact log records zero
total tokens. OOD176 rows are recomputed from `in_tok`, `out_tok`, and the same
pricing table.

The tables above were originally rebuilt from nested source matrices by
`scripts/export_coderouterbench.py`, which no longer exists in this
repository; the checked-in tables remain as-is.

## Agentic Artifacts

For automated readers and coding agents, `agentic-artifacts/` provides a compact
research-artifact entry layer over this repository. Start with:

```text
agentic-artifacts/PAPER.md
agentic-artifacts/manifest.json
agentic-artifacts/evidence/tables/score_matrix.md
```

That folder mirrors the important claims, experiment scope, key metrics,
baseline table, matrix pointers, and design trace using small Markdown, JSON,
YAML, CSV, and HTML files. It does not replace the canonical data under
`data/` or reference outputs under `outputs/`; it points to them with relative
paths so agents can load only the evidence they need. Note that any
reproduction commands referenced there assume the now-removed Python
pipeline.

## Repository Layout

```text
agentic-artifacts/               Agent-readable manifest, claims, evidence map
src/TotallyHotArcRouter*/              .NET router implementation, GUI, sandbox, tests

data/coderouterbench/            Canonical CodeRouterBench task x model tables
data/id/                         Phase-1 compact ID labels, splits, tokens
data/ood/                        Legacy OOD112 matrix, patches, verifier cache
data/matrices/phase1_TotallyHotArcRouter_v2   Phase-1 observation/response matrices
data/matrices/phase2_ood/        Old112, New64, and unified OOD176 matrices
data/baseline_inputs/            Baseline decisions/checkpoints for OOD176 replay
outputs/                         Checked-in reference outputs
```

## CodeRouterBench Data

The release keeps data needed for offline scoring and reproduction. The
canonical public benchmark files are in `data/coderouterbench/`:

- `id_results_long.csv`: one row per ID task/model result.
- `id_probing_results_long.csv`: original train + validation merged into the
  probing set.
- `id_test_results_long.csv`: held-out ID test set, labeled `id_test`.
- `ood176_results_long.csv`: one row per OOD176 task/model result.
- `id_tasks.jsonl` and `ood176_tasks.jsonl`: task metadata.
- `models.json`: the eight canonical backend models and USD pricing metadata.
- `README.md`: a compact dataset card for Hugging Face Dataset uploads.

The nested source matrices remain available for audit:

- `data/matrices/phase1_acrouter_v2/obs_matrix_clean.json` is the complete
  9,999-task x 8-model ID observation matrix.
- `data/matrices/phase1_acrouter_v2/response_matrix.json` stores the compact
  phase-1 response matrix used by the reproduction bundle.
- `data/matrices/phase2_ood/unified/matrix_acrouter_ood176.json` is the
  complete 176-task x 8-model OOD176 scoring matrix.
- `data/matrices/phase2_ood/raw/new64/matrix.json` records the filtered New64
  subset: FeatureBench 49 + LongCLI 14 + SWE-CI 1. The excluded 8 SWE-CI task
  IDs are recorded in the same JSON file.
- `data/id/` contains task dimensions, train/val/test splits, legacy compact
  labels, token counts, and saved voter decisions. Prefer
  `data/coderouterbench/id_results_long.csv` for public benchmark consumption.
- `data/ood/` contains the legacy OOD112 SWE-MiniSandbox matrix, patch-only
  model submissions, and a hash-checked sandbox cache for supplementary
  reproduction.

Rebuilding these matrices, and republishing the dataset or the trained router
adapter to Hugging Face, previously used `scripts/build_ood176_dataset.py` and
two upload shell scripts. All of that tooling has been removed along with the
rest of the Python pipeline; republishing would need it restored first.

## Notes And Caveats

- No API keys are required. The release does not call external model APIs.
- The Python maintainer scripts that rebuilt compact data from raw local
  experiment outputs have been removed along with the rest of the Python
  pipeline.

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

